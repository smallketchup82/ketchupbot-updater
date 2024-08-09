using System.CommandLine;
using System.Reflection;
using ketchupbot_updater.API;
using ketchupbot_updater.Jobs;
using Microsoft.Extensions.Configuration;
using Quartz;
using Quartz.Impl;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ketchupbot_updater;

/// <summary>
/// Entrypoint, configuration, and initialization for the updater component
/// </summary>
public static class Program
{
    public static bool DryRun { get; private set; }

    private static async Task<int> Main(string[] args)
    {
        #region Options

        var shipsOption = new Option<string[]>(
            ["--ships", "-s"],
            () => ["all"],
            """List of ships to update. "all" to update all ships, "none" to not update ships. Must be "all" in order to use the scheduler"""
        )
        {
            AllowMultipleArgumentsPerToken = true
        };

        var turretsOption = new Option<bool>(
            ["-t", "--turrets"],
            "Update turrets?"
        );

        var shipScheduleOption = new Option<string>(
            ["-ss", "--ship-schedule"],
            "Pass a schedule (in cron format specialized for quartz) to enable the ship scheduler. Use https://www.freeformatter.com/cron-expression-generator-quartz.html to generate a cron expression. Can pass Will ignore --ships option. This takes precedence over the environment variable"
        );

        var turretScheduleOption = new Option<string>(
            ["-ts", "--turret-schedule"],
            "Pass to enable the turret scheduler. Will ignore the --turrets option. This takes precedence over the environment variable"
        );

        var dryRunOption = new Option<bool>(
            "--dry-run",
            "Pass to enable dry run. Only works in production due to some stupid technical foresight. This takes precedence over the environment variable"
        );

        var threadCountOption = new Option<int>(
            "--threads",
            getDefaultValue: () => 0,
            "Number of threads to use when updating ships. Set to 1 for single threaded execution, 0 for automatic (let the the .NET runtime manage the thread count dynamically).");

        var secretsDirectoryOption = new Option<string>(
            "--secrets-directory",
            getDefaultValue: () => AppContext.BaseDirectory,
            "Directory where the appsettings.json file is located. Defaults to the directory holding the executable."
        );

        var verboseOption = new Option<bool>(
            ["-v", "--verbose"],
            "Enable verbose logging"
        );

        #endregion

        var rootCommand = new RootCommand("KetchupBot Updater Component")
        {
            shipsOption,
            turretsOption,
            shipScheduleOption,
            turretScheduleOption,
            dryRunOption,
            threadCountOption,
            secretsDirectoryOption,
            verboseOption
        };

        var levelSwitch = new LoggingLevelSwitch();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .Enrich.WithThreadId()
            .WriteTo.Console()
            .CreateLogger();

        rootCommand.SetHandler(async handler =>
        {
            await using (Stream? stream = Assembly.GetExecutingAssembly()
                             .GetManifestResourceStream("ketchupbot_updater.Assets.banner.txt"))
            {
                if (stream == null)
                    throw new InvalidOperationException("Failed to load banner");

                using (var reader = new StreamReader(stream)) Console.WriteLine(await reader.ReadToEndAsync());
            }

            Console.WriteLine(
                $"\nketchupbot-updater | v{Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Development"} | {DateTime.Now}\n");

            #region Configuration

            IConfigurationBuilder builder = new ConfigurationBuilder()
                .SetBasePath(handler.ParseResult.GetValueForOption(secretsDirectoryOption) ?? AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables();

            IConfigurationRoot configuration = builder.Build();

#if DEBUG
            Log.Information("Running in development mode");
            levelSwitch.MinimumLevel = LogEventLevel.Debug;
            DryRun = true;
#else
            DryRun = handler.ParseResult.GetValueForOption(dryRunOption);
            if (DryRun) Log.Information("Running in dry run mode");
#endif

            if (handler.ParseResult.GetValueForOption(verboseOption))
            {
                levelSwitch.MinimumLevel = LogEventLevel.Verbose;
                Log.Information("Enabled verbose logging");
            }

            #endregion

            var mwClient = new MwClient(configuration["MWUSERNAME"] ?? throw new InvalidOperationException("MWUSERNAME not set"),
                configuration["MWPASSWORD"] ?? throw new InvalidOperationException("MWPASSWORD not set"));
            Log.Information("Logged into the Galaxypedia");
            var apiManager = new ApiManager(configuration["GIAPI_URL"] ?? throw new InvalidOperationException("GIAPI_URL not set"));

            #region Scheduling Logic

            // TODO: Probably should refactor this logic. We kinda repeat ourselves here. It'd be better to move the job
            // creation logic outside of the if statements, and instead use the if statements for defining triggers. For
            // example, if the user doesn't specify a cron schedule, we can still use the Job to run the methods via
            // StartNow. Like a fire & forget. So we should probably remove the logic at the end of this method that
            // runs the one-off logic, and instead use the jobs for one-off logic. Though, this might not be possible
            // for running for individual ships.

            string? shipScheduleOptionValue = handler.ParseResult.GetValueForOption(shipScheduleOption);
            string? turretScheduleOptionValue = handler.ParseResult.GetValueForOption(turretScheduleOption);

            if (shipScheduleOptionValue != null || turretScheduleOptionValue != null)
            {
                IScheduler scheduler = await new StdSchedulerFactory().GetScheduler();
                await scheduler.Start();

                if (shipScheduleOptionValue != null)
                {
                    IJobDetail massUpdateJob = JobBuilder.Create<MassUpdateJob>()
                        .WithIdentity("massUpdateJob", "group1")
                        .Build();

                    massUpdateJob.JobDataMap.Put("shipUpdater", new ShipUpdater(mwClient, apiManager));

                    ITrigger massUpdateTrigger = TriggerBuilder.Create()
                        .WithIdentity("massUpdateTrigger", "group1")
                        .ForJob("massUpdateJob", "group1")
                        .StartNow()
                        .WithCronSchedule(shipScheduleOptionValue)
                        .Build();

                    await scheduler.ScheduleJob(massUpdateJob, massUpdateTrigger);
                    Console.WriteLine($"Scheduled ship mass update job for {massUpdateTrigger.GetNextFireTimeUtc()?.ToLocalTime()}");
                    Console.WriteLine("Running mass update job now...");
                    await scheduler.TriggerJob(new JobKey("massUpdateJob", "group1"));
                }

                if (turretScheduleOptionValue != null)
                {
                    IJobDetail turretUpdateJob = JobBuilder.Create<TurretUpdateJob>()
                        .WithIdentity("turretUpdateJob", "group1")
                        .Build();

                    turretUpdateJob.JobDataMap.Put("turretUpdater", new TurretUpdater(mwClient, apiManager));

                    ITrigger turretUpdateTrigger = TriggerBuilder.Create()
                        .WithIdentity("turretUpdateTrigger", "group1")
                        .ForJob("turretUpdateJob", "group1")
                        .StartNow()
                        .WithCronSchedule(turretScheduleOptionValue)
                        .Build();

                    await scheduler.ScheduleJob(turretUpdateJob, turretUpdateTrigger);
                    Console.WriteLine("Scheduled turret update job");
                }

                // Keep application running until it's manually stopped. The scheduler will never stop by itself.
                await Task.Delay(-1);
            }

            #endregion

            #region Ship Option Handler

            string[]? shipsOptionValue = handler.ParseResult.GetValueForOption(shipsOption);
            if (shipsOptionValue != null && shipsOptionValue.First() != "none" && shipScheduleOptionValue == null)
            {
                var shipUpdater = new ShipUpdater(mwClient, apiManager);

                if (shipsOptionValue.First() == "all")
                    await shipUpdater.UpdateAllShips();
                else
                {
                    await shipUpdater.MassUpdateShips(shipsOptionValue.ToList());
                }
            }

            #endregion

            #region Turret Option Handler

            bool turrets = handler.ParseResult.GetValueForOption(turretsOption);
            if (turrets) await new TurretUpdater(mwClient, apiManager).UpdateTurrets();

            #endregion

            await Log.CloseAndFlushAsync();
        });

        return await rootCommand.InvokeAsync(args);
    }
}