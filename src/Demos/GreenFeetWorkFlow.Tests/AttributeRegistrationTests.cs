﻿namespace GreenFeetWorkflow.Tests;

public class AttributeRegistrationTests
{
    private static readonly Dictionary<string, string?> StepResult = new();

    [Test]
    public void When_using_stepnameattribute_Then_stepimplementation_is_registered()
    {
        var testhelper = new TestHelper();

        testhelper.CreateAndRunEngineWithAttributes(new Step(StepA.Name) { FlowId = testhelper.FlowId });

        StepResult[testhelper.FlowId].Should().Be(testhelper.FlowId);

        testhelper.AssertTableCounts(testhelper.FlowId, ready: 0, done: 2, failed: 0);
    }

    [StepName(Name)]
    internal class StepA : IStepImplementation
    {
        public const string Name = "v1/AttributeRegistrationTest/a";

        public async Task<ExecutionResult> ExecuteAsync(Step step)
        {
            return await step.DoneAsync(new Step(StepB.Name));
        }
    }

    [StepName(Name)]
    internal class StepB : IStepImplementation
    {
        public const string Name = "v1/AttributeRegistrationTest/b";

        public async Task<ExecutionResult> ExecuteAsync(Step step)
        {
            StepResult.Add(step.FlowId!, step.FlowId);
            return await step.DoneAsync();
        }
    }
}