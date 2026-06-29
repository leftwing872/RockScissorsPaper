using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
public class AWSCloudWatch
        {
            //private readonly IConfiguration _configuration;
            private static System.Timers.Timer aTimer;

            // public AWSCloudWatch(IConfiguration configuration)
            // {
            //     _configuration = configuration;
            // }

            // public void TimerStart()
            // {
            //     Console.WriteLine("\nPress the Enter key to exit the application...\n");
            //     Console.WriteLine("The application started at {0:HH:mm:ss.fff}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            //     aTimer = new System.Timers.Timer(4000); // method executes every 4 seconds
            //     aTimer.Elapsed += (s, e) => CloudWatchLog();
            //     aTimer.AutoReset = true;
            //     aTimer.Enabled = true;
            //     Console.ReadLine();
            //     aTimer.Stop();
            //     aTimer.Dispose();
            //     Console.WriteLine("Terminating the application...");

            //     while (Console.Read() != 'q') ;

            // }

            public async void CloudWatchLog(String log)
            {
                try
                {
                    //var credentials = new Amazon.Runtime.BasicAWSCredentials(_configuration["Client_id"], _configuration["Client_secret"]);
                    //var credentials = new Amazon.Runtime.Ba.BasicAWSCredentials();

                    var config = new AmazonCloudWatchLogsConfig
                    {
                        RegionEndpoint = Amazon.RegionEndpoint.APNortheast1
                    };

                    var logClient = new AmazonCloudWatchLogsClient(config);

                    await logClient.PutLogEventsAsync(new PutLogEventsRequest()
                    {
                        LogGroupName = "Take1Server",//_configuration["LogGroupName"],
                        LogStreamName = "Stream1",//_configuration["LogStreamName"],
                        LogEvents = new List<InputLogEvent>()
                        {
                            new InputLogEvent()
                            {
                                Message = log,
                                Timestamp = DateTime.UtcNow
                            }
                        }

                    });
                    //Console.WriteLine("Logging successfull");
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message, "Error occured");
                    Console.WriteLine("Original log: {0}", log);
                }

            }
        }