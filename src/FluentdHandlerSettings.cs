namespace Serilog.Sinks.Fluentd.Core
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography.X509Certificates;

    public class FluentdHandlerSettings
    {
        public string Tag { get; set; } = "";

        public string Host { get; set; } = "logs.logsense.com";

        public int Port { get; set; } = 32714;

        public int TCPSendTimeout { get; set; } = 3000;

        public TimeSpan BatchingPeriod { get; set; } = TimeSpan.FromSeconds(2);

        public int BatchPostingLimit { get; set; } = 50;

        public int TCPRetryAmount { get; set; } = 5;

        public int LingerTime { get; set; } = 5;

        public bool UsingSsl { get; set; } = true;

        public string customer_token { get; set; } = "";

        public string source_name { get; set; } = ".NET";

        public string pattern_key { get; set; } = "";
    }
}
