﻿namespace GreenFeetWorkflow.WebApiDemo;


public class WorkflowStarter : BackgroundService
{
    readonly WorkflowEngine engine;

    public WorkflowStarter(WorkflowEngine engine)
    {
        this.engine = engine;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        engine.Data.AddStep(new Step(StepFetchWeatherForecast.Name) { Singleton = true });

        await engine.StartAsync(new WorkflowConfiguration(new WorkerConfig(), NumberOfWorkers: 1), stoppingToken: stoppingToken);
    }
}
