using NLog.Common;
using NLog.Config;
using SharpRaven;
using SharpRaven.Data;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

// ReSharper disable CheckNamespace
namespace NLog.Targets
// ReSharper restore CheckNamespace
{
    [Target("Sentry")]
    public class SentryTarget : TargetWithLayout
    {
        private Dsn dsn;
        private TimeSpan clientTimeout;
        private readonly Lazy<IRavenClient> client;
        private static readonly string rootAssemblyVersion;

        private const string RawStackTraceKey = "RawStackTrace";
        private const string ServiceNameKey = "ServiceName";
        private const string DefaultRavenEnvironment = "develop";
        private const int DefaultRavenTimeoutSeconds = 10;

        /// <summary>
        /// Map of NLog log levels to Raven/Sentry log levels
        /// </summary>
        protected static readonly IDictionary<LogLevel, ErrorLevel> LoggingLevelMap = new Dictionary<LogLevel, ErrorLevel>
        {
            { LogLevel.Debug, ErrorLevel.Debug },
            { LogLevel.Error, ErrorLevel.Error },
            { LogLevel.Fatal, ErrorLevel.Fatal },
            { LogLevel.Info, ErrorLevel.Info },
            { LogLevel.Trace, ErrorLevel.Debug },
            { LogLevel.Warn, ErrorLevel.Warning },
        };

        static SentryTarget()
        {
            var entryAssembly = Assembly.GetEntryAssembly();

            if (null != entryAssembly)
            {
                rootAssemblyVersion = entryAssembly.GetName().Version.ToString();
            }
        }

        /// <summary>
        /// The DSN for the Sentry host
        /// </summary>
        [RequiredParameter]
        public string Dsn
        {
            get { return dsn?.ToString(); }
            set { dsn = new Dsn(value); }
        }

        /// <summary>
        /// Determines service name that causes current event
        /// </summary>
        public string ServiceName { get; set; }

        /// <summary>
        /// Gets or sets the timeout for the Raven client.
        /// </summary>
        public string Timeout
        {
            get { return clientTimeout.ToString("c"); }
            set { clientTimeout = TimeSpan.ParseExact(value, "c", CultureInfo.InvariantCulture); }
        }

        /// <summary>
        /// Determines whether events with no exceptions will be send to Sentry or not
        /// </summary>
        public bool IgnoreEventsWithNoException { get; set; }

        /// <summary>
        /// Determines whether event properties will be sent to sentry as Tags or not
        /// </summary>
        public bool SendLogEventInfoPropertiesAsTags { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        public SentryTarget()
        {
            client = new Lazy<IRavenClient>(DefaultClientFactory);
        }

        /// <summary>
        /// Internal constructor, used for unit-testing
        /// </summary>
        /// <param name="ravenClient">A <see cref="IRavenClient"/></param>
        internal SentryTarget(IRavenClient ravenClient)
        {
            client = new Lazy<IRavenClient>(() => ravenClient);
        }

        /// <summary>
        /// Writes logging event to the log target.
        /// </summary>
        /// <param name="logEvent">Logging event to be written out.</param>
        protected override void Write(LogEventInfo logEvent)
        {
            try
            {
                Dictionary<string, string> extras = SendLogEventInfoPropertiesAsTags
                    ? null
                    : logEvent.Properties.ToDictionary(x => x.Key.ToString(), x => x.Value.ToString());

                client.Value.Logger = logEvent.LoggerName;

                // If the log event did not contain an exception and we're not ignoring
                // those kinds of events then we'll send a "Message" to Sentry
                if (logEvent.Exception == null && !IgnoreEventsWithNoException)
                {
                    var sentryMessage = new SentryMessage(Layout.Render(logEvent));
                    var sentryEvent = new SentryEvent(sentryMessage)
                    {
                        Level = LoggingLevelMap[logEvent.Level],
                        Extra = extras,
                        Fingerprint = { logEvent.Message, logEvent.UserStackFrame?.ToString(), logEvent.LoggerName },
                        Tags = { { ServiceNameKey, ServiceName } }
                    };

                    client.Value.Capture(sentryEvent);
                }
                else if (logEvent.Exception != null)
                {
                    var sentryMessage = new SentryMessage(logEvent.FormattedMessage);
                    var sentryEvent = new SentryEvent(logEvent.Exception)
                    {
                        Extra = new Dictionary<string, string> { { RawStackTraceKey, logEvent.Exception.StackTrace } },
                        Level = LoggingLevelMap[logEvent.Level],
                        Message = sentryMessage,
                        Fingerprint = { logEvent.Message, logEvent.UserStackFrame?.ToString(), logEvent.LoggerName },
                        Tags = { { ServiceNameKey, ServiceName } }
                    };

                    client.Value.Capture(sentryEvent);
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
            }
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing">True to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && client.IsValueCreated)
            {
                var ravenClient = client.Value as RavenClient;

                if (ravenClient != null)
                {
                    ravenClient.ErrorOnCapture = null;
                }
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Implements the default client factory behavior.
        /// </summary>
        /// <returns>New instance of a RavenClient.</returns>
        private IRavenClient DefaultClientFactory()
        {
            var ravenClient = new RavenClient(dsn)
            {
                ErrorOnCapture = LogException,
                Timeout = clientTimeout,
                Release = rootAssemblyVersion
            };

            if (string.IsNullOrWhiteSpace(ravenClient.Environment))
            {
                ravenClient.Environment = DefaultRavenEnvironment;
            }

            if (TimeSpan.Zero == ravenClient.Timeout)
            {
                ravenClient.Timeout = TimeSpan.FromSeconds(DefaultRavenTimeoutSeconds);
            }

            return ravenClient;
        }

        /// <summary>
        /// Logs an exception using the internal logger class.
        /// </summary>
        /// <param name="ex">The ex to log to the internal logger.</param>
        private void LogException(Exception ex)
        {
            InternalLogger.Error("Unable to send Sentry request: {0}", ex.Message);
        }
    }
}
