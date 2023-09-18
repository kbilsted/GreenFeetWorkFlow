﻿using System.Transactions;

namespace GreenFeetWorkflow;

public class WorkflowRuntimeData
{
    private readonly IWorkflowIocContainer iocContainer;
    private readonly IWorkflowStepStateFormatter formatter;
    private readonly IWorkflowLogger logger;

    public WorkflowRuntimeData(IWorkflowIocContainer iocContainer, IWorkflowStepStateFormatter formatter, IWorkflowLogger logger)
    {
        this.iocContainer = iocContainer;
        this.formatter = formatter;
        this.logger = logger;
    }

    /// <summary> Reschedule a ready step to 'now' and send it activation data </summary>
    public int ActivateStep(int id, object? activationArguments, object? transaction = null)
    {
        var persister = iocContainer.GetInstance<IStepPersister>();

        int rows = persister.InTransaction(() =>
            {
                var step = persister.SearchSteps(new SearchModel(Id: id), StepStatus.Ready).FirstOrDefault();
                if (step == null)
                    return 0;

                step.ScheduleTime = TrimToSeconds(DateTime.Now);
                step.ActivationArgs = formatter.Serialize(activationArguments);
                return persister.Update(StepStatus.Ready, step);
            },
            transaction);
        return rows;
    }

    /// <summary> Add step to be executed. May throw exception if persistence layer fails. For example when inserting multiple singleton elements </summary>
    /// <returns>the identity of the step</returns>
    public int AddStep(Step step, object? transaction = null) => AddSteps(new[] { step }, transaction).Single();

    /// <summary> Add steps to be executed. May throw exception if persistence layer fails. For example when inserting multiple singleton elements </summary>
    /// <returns>the identity of the steps</returns>
    public int[] AddSteps(Step[] steps, object? transaction = null)
    {
        var now = DateTime.Now;

        foreach (var x in steps)
        {
            FixupNewStep(null, x, now);
        }

        IStepPersister persister = iocContainer.GetInstance<IStepPersister>();
        return persister.InTransaction(() => persister.Insert(StepStatus.Ready, steps), transaction);
    }

    internal void FixupNewStep(Step? originStep, Step step, DateTime now)
    {
        if (string.IsNullOrEmpty(step.Name))
            throw new NullReferenceException("step name cannot be null or empty");

        step.CreatedTime = now;
        step.CreatedByStepId = originStep?.Id ?? 0;

        step.FlowId ??= originStep?.FlowId ?? Guid.NewGuid().ToString();

        step.CorrelationId ??= originStep?.CorrelationId;

        if (step.ScheduleTime == default)
            step.ScheduleTime = TrimToSeconds(now);

        FormatStateForSerialization(step);
    }

    public List<Step> SearchSteps(SearchModel criteria, StepStatus target, object? transaction = null)
    {
        IStepPersister persister = iocContainer.GetInstance<IStepPersister>();
        var result = persister.InTransaction(() => persister.SearchSteps(criteria, target), transaction);
        return result;
    }

    public Dictionary<StepStatus, List<Step>> SearchSteps(SearchModel criteria, FetchLevels fetchLevels, object? transaction = null)
    {
        IStepPersister persister = iocContainer.GetInstance<IStepPersister>();
        var result = persister.InTransaction(() => persister.SearchSteps(criteria, fetchLevels), transaction);
        return result;
    }

    /// <summary> Re-execute steps that are 'done' or 'failed' by inserting a clone into the 'ready' queue </summary>
    /// <returns>Ids of inserted steps</returns>
    public int[] ReExecuteSteps(SearchModel criteria, object? transaction = null)
    {
        IStepPersister persister = iocContainer.GetInstance<IStepPersister>();

        int[] ids = persister.InTransaction(() =>
            {
                var now = DateTime.Now;

                var entities = persister.SearchSteps(criteria, FetchLevels.NONREADY);
                
                var steps = entities
                .SelectMany(x => x.Value)
                .Select(step => new Step()
                {
                    FlowId = step.FlowId,
                    CorrelationId = step.CorrelationId,
                    CreatedByStepId = step.CreatedByStepId,
                    CreatedTime = now,
                    Description = $"Re-execution of step id: " + step.Id,
                    State = step.State,
                    StateFormat = step.StateFormat,
                    ActivationArgs = step.ActivationArgs,
                    ScheduleTime = now,
                    Singleton = step.Singleton,
                    SearchKey = step.SearchKey,
                    Name = step.Name,
                })
                .ToArray();

                if (logger.InfoLoggingEnabled)
                    logger.LogInfo("Reexecuting ids", null, new Dictionary<string, object?>() { { "ids", steps.Select(x => x.Id).ToArray() } });

                return persister.Insert(StepStatus.Ready, steps);
            },
            transaction);

        return ids;
    }

    public bool FailStep(int id)
    {
        IStepPersister persister = iocContainer.GetInstance<IStepPersister>();
        return persister.InTransaction(() =>
        {
            var step = persister.SearchSteps(new(Id: id), StepStatus.Ready).SingleOrDefault();
            if (step == null)
                return false;
            
            persister.Delete(StepStatus.Ready, id);
            persister.Insert(StepStatus.Failed, step);
            return true;
        });
    }
        

    /// <summary> we round down to ensure a worker can pick up the step/rerun-step. if in unittest mode it may exit if not rounded. </summary>
    internal static DateTime? TrimToSeconds(DateTime? now) => now == null ? null : TrimToSeconds(now.Value);

    /// <summary> we round down to ensure a worker can pick up the step/rerun-step. if in unittest mode it may exit if not rounded. </summary>
    internal static DateTime TrimToSeconds(DateTime now) => new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);

    internal void FormatStateForSerialization(Step step)
    {
        if (step.InitialState == null)
        {
            step.State = null;
            return;
        }

        if (step.StateFormat == null || step.StateFormat == formatter.StateFormatName)
        {
            step.State = formatter.Serialize(step.InitialState);
        }
        else
        {
            throw new Exception($"No formatter registered for format name '{step.StateFormat}'");
        }
    }
}