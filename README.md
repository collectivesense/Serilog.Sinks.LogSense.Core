# Serilog.Sinks.Fluentd.Core

Sends your logs to Logsense via [Fluentd](https://www.fluentd.org/) protocol.

Based on https://github.com/VQComms/Serilog.Sinks.Fluentd.Core

```csharp
            var log = new LoggerConfiguration()
                .WriteTo.Fluentd(new FluentdHandlerSettings
                {
                    customer_token = "ENTER TOKEN HERE"                  
                })
                .CreateLogger();
```
