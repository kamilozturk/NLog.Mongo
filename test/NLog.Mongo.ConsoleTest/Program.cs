using System;
using System.Collections.Generic;

namespace NLog.Mongo.ConsoleTest
{
    public class Program
    {
        private static readonly NLog.Logger _logger = NLog.LogManager.GetCurrentClassLogger();
        
        public static void Main(string[] args)
        {
            var logEventInfo = new LogEventInfo();
            logEventInfo.Level = LogLevel.Info;

            logEventInfo.Properties.Add("stringValue", "Kamil ÖZTÜRK");
            logEventInfo.Properties.Add("arrayValue", new object[] { 1, "Kamil", DateTime.Now, true, 2.5 });
            logEventInfo.Properties.Add("dictionaryValue", new Dictionary<string, object>() { { "columnA", "value A" }, { "columnB", DateTime.Now } });
            logEventInfo.Properties.Add("objectValue", new { name = "Kamil", lastname = "OZTURK" });
            logEventInfo.Properties.Add("integerValue", 1);
            //if server supports
            //logEventInfo.Properties.Add("decimalValue", 2.5m);
            logEventInfo.Properties.Add("doubleValue", 3.5d);
            logEventInfo.Properties.Add("dateValue", DateTime.UtcNow);
            logEventInfo.Properties.Add("booleanValue", true);

            var logger = LogManager.GetCurrentClassLogger();
            logger.Log(logEventInfo);

            Console.ReadLine();
        }
    }
}
