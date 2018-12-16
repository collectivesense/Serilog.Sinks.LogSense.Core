namespace Serilog.Sinks.Fluentd.Core.Sinks
{
    using System;
    using System.Collections.Generic;
    using System.Dynamic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Net.Security;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Security.Authentication;
    using System.Security.Cryptography.X509Certificates;
    using System.Threading.Tasks;
    using MsgPack;
    using MsgPack.Serialization;
    using Serilog.Debugging;
    using Serilog.Events;
    using Serilog.Sinks.PeriodicBatching;

    public class FluentdSink : PeriodicBatchingSink
    {
        private readonly SemaphoreSlim semaphore = new SemaphoreSlim(1);

        private readonly FluentdHandlerSettings settings;

        private readonly SerializationContext serializationContext;

        private readonly SerilogVisitor visitor;

        private TcpClient client;

        private SslStream sslStream;

        private Stream stream;

        private String sourceIP;

        public FluentdSink(FluentdHandlerSettings settings) : base(settings.BatchPostingLimit, settings.BatchingPeriod)
        {
            this.settings = settings;

            if (String.IsNullOrEmpty(this.settings.customer_token) || this.settings.customer_token.Length < 30) 
            {
                throw new ArgumentException("Customer token property must be provided with a valid value!");
            }
            
            this.serializationContext = new SerializationContext(PackerCompatibilityOptions.PackBinaryAsRaw) { SerializationMethod = SerializationMethod.Map };
            this.visitor = new SerilogVisitor();
            this.sourceIP = this.DetermineIP();
        }

        private string DetermineIP()
        {
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
            {
                socket.Connect("8.8.8.8", 65530);
                IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                var localIP = endPoint.Address.ToString();
                SelfLog.WriteLine("Determined source IP address to: {0}", localIP);
                return localIP;
            }

            return null;
        }

        protected override async Task EmitBatchAsync(IEnumerable<LogEvent> events)
        {
            await this.Send(events);
        }
        
        private async Task Send(IEnumerable<LogEvent> messages)
        {
            foreach (var logEvent in messages)
            {
                using (var sw = new MemoryStream())
                {
                    try
                    {
                        var packer = Packer.Create(sw);
                        await packer.PackArrayHeaderAsync(3); //3 fields to store
                        await packer.PackStringAsync(this.settings.Tag, Encoding.UTF8);
                        await packer.PackAsync((ulong)logEvent.Timestamp.ToUnixTimeSeconds());

                        this.FormatwithMsgPack(logEvent, packer);
                    }
                    catch (Exception ex)
                    {
                        SelfLog.WriteLine(ex.ToString());
                        continue;
                    }

                    var retryLimit = this.settings.TCPRetryAmount;

                    while (retryLimit > 0)
                    {
                        try
                        {
                            await this.Connect();
                            await this.semaphore.WaitAsync();
                            await this.PrepareStream();
                            
                            var data = sw.ToArray();

                            await stream.WriteAsync(data, 0, data.Length);
                            await stream.FlushAsync();
                            
                            break;
                        }
                        catch (Exception ex)
                        {
                            this.Disconnect();
                            SelfLog.WriteLine(ex.ToString());
                        }
                        finally
                        {
                            this.semaphore.Release();
                            retryLimit--;
                        }
                    }
                }
            }
        }

        private async Task Connect()
        {
            if (this.client != null)
            {
                if (!this.client.Connected)
                {
                    this.client.Dispose();
                    this.client = null;
                    this.stream = null;
                    this.sslStream = null;
                }
                else
                {
                    return;
                }
            }

            this.client = new TcpClient
            {
                SendTimeout = this.settings.TCPSendTimeout,
                LingerState = new LingerOption(true, this.settings.LingerTime)
            };

            await this.client.ConnectAsync(this.settings.Host, this.settings.Port);
        }


        private async Task PrepareStream()
        {
            if (this.stream != null)
            {
                return;
            }
            
            if (this.settings.UsingSsl)
            {
                this.sslStream = new SslStream(
                    this.client.GetStream(),
                    false);
                                
                try 
                {
                    this.sslStream.AuthenticateAsClient(this.settings.Host, null, SslProtocols.Tls12, true);
                } 
                catch (AuthenticationException e)
                {
                    SelfLog.WriteLine("Exception: {0}", e.Message);
                    if (e.InnerException != null)
                    {
                        SelfLog.WriteLine("Inner exception: {0}", e.InnerException.Message);
                    }
                    SelfLog.WriteLine ("Authentication failed - closing the connection.");
                    this.Disconnect();
                    
                    return;
                }

                this.stream = this.sslStream;
            }
            else
            {
                this.stream = this.client.GetStream();
            }
        }
        
        private void FormatwithMsgPack(LogEvent logEvent, Packer packer)
        {
            dynamic localEvent = new ExpandoObject();

            localEvent.cs_customer_token = this.settings.customer_token;
            localEvent.source_name = this.settings.source_name;

            if (!String.IsNullOrEmpty(this.settings.pattern_key))
            {
                localEvent.cs_pattern_key = this.settings.pattern_key;
            }
            if (this.sourceIP != null)
            {
                localEvent.cs_src_ip = this.sourceIP;
            }

            localEvent.time_stamp = new DateTimeOffset(logEvent.Timestamp.UtcDateTime).ToUnixTimeMilliseconds();
            localEvent.message_template = logEvent.MessageTemplate.Text;
            localEvent.message = logEvent.RenderMessage();
            localEvent.level = logEvent.Level.ToString();

            if (logEvent.Exception != null)
            {
                localEvent.exceptions = new List<LocalException>();
                localEvent.exceptions.Add(new LocalException());
                WriteMsgPackException(logEvent.Exception, localEvent);
            }

            foreach (var property in logEvent.Properties)
            {
                var name = property.Key;
                if (name.Length > 0 && name[0] == '@')
                {
                    // Escape first '@' by doubling
                    name = '@' + name;
                }

                this.visitor.Visit((IDictionary<string, object>)localEvent, name, property.Value);
            }

            packer.Pack((IDictionary<string, object>)localEvent, this.serializationContext);
        }

        /// <summary>
        /// Writes out the attached exception
        /// </summary>
        private void WriteMsgPackException(Exception exception, dynamic localLogEvent)
        {
            this.WriteMsgPackExceptionSerializationInfo(exception, 0, localLogEvent);
        }

        private void WriteMsgPackExceptionSerializationInfo(Exception exception, int depth, dynamic localLogEvent)
        {
            if (depth > 0)
            {
                localLogEvent.exceptions.Add(new LocalException());
            }

            this.WriteMsgPackSingleException(exception, depth, localLogEvent.exceptions[depth]);

            if (exception.InnerException != null && depth < 20)
            {
                this.WriteMsgPackExceptionSerializationInfo(exception.InnerException, ++depth, localLogEvent);
            }
        }

        private void WriteMsgPackSingleException(Exception exception, int depth, dynamic localException)
        {
            var helpUrl = exception.HelpLink;
            var stackTrace = exception.StackTrace ?? "";
            var hresult = exception.HResult;
            var source = exception.Source;

            localException.Depth = depth;
            localException.Message = exception.Message;
            localException.Source = source;
            localException.StackTraceString = stackTrace;
            localException.HResult = hresult;
            localException.HelpURL = helpUrl;
        }

        private void Disconnect()
        {
            this.sslStream?.Dispose();
            this.client?.Dispose();
            this.client = null;
            this.sslStream = null;
            this.stream = null;
        }

        protected override void Dispose(bool disposing)
        {
            this.Disconnect();

            base.Dispose(disposing);
        }
    }
}
