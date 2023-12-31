﻿using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using ReassureTest;
using System.Diagnostics;

namespace GreenFeetWorkflow.Tests;

public class WorkerTests
{
    TestHelper helper = new TestHelper();
    private readonly WorkflowConfiguration cfg = new WorkflowConfiguration(
        new WorkerConfig()
        {
            StopWhenNoImmediateWork = true
        },
        NumberOfWorkers: 1);

    [SetUp]
    public void Setup()
    {
        helper = new TestHelper();
    }

    [Test]
    public void When_executing_OneStep_with_state_Then_succeed()
    {
        string? stepResult = null;

        helper.CreateAndRunEngine(
            new[] { new Step("OneStep") { InitialState = 1234, FlowId = helper.FlowId } },
            ("OneStep", new GenericImplementation(step =>
            {
                int counter = helper.Formatter!.Deserialize<int>(step.State);
                stepResult = $"hello {counter}";
                return ExecutionResult.Done();
            })));

        stepResult.Should().Be("hello 1234");
        helper.AssertTableCounts(helper.FlowId, ready: 0, done: 1, failed: 0);
    }

    [Test]
    public async Task When_adding_two_steps_in_the_same_transaction_Then_succeed()
    {
        string[] stepResults = new string[2];
        const string name = "v1/When_adding_two_steps_in_the_same_transaction_Then_succeed";

        var engine = helper.CreateEngine(
            (name, new GenericImplementation(step =>
            {
                int counter = helper.Formatter!.Deserialize<int>(step.State);
                stepResults[counter] = $"hello {counter}";
                return ExecutionResult.Done();
            })));

        await using var connection = new SqlConnection(helper.ConnectionString);
        connection.Open();
        await using var tx = connection.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);
        await engine.Data.AddStepAsync(new Step(name, 0), tx);
        await engine.Data.AddStepAsync(new Step(name, 1), tx);
        tx.Commit();
        engine.Start(cfg);

        stepResults.Should().BeEquivalentTo(new[] { "hello 0", "hello 1" });
    }

    [Test]
    public void When_executing_step_throwing_special_FailCurrentStepException_Then_fail_current_step()
    {
        helper.CreateAndRunEngine(
             new Step() { Name = "test-throw-failstepexception", FlowId = helper.FlowId },
            (
                "test-throw-failstepexception",
                GenericImplementation.Create(step => throw new FailCurrentStepException("some description"))
            ));

        helper.AssertTableCounts(helper.FlowId, ready: 0, done: 0, failed: 1);
        helper.GetByFlowId(helper.FlowId).Description.Should().Be("some description");
    }

    [Test]
    public void When_executing_step_throwing_special_FailCurrentStepException_using_step_Then_fail_current_step()
    {
        helper.CreateAndRunEngine(
            new Step("test-throw-failstepexception_from_step_variable") { FlowId = helper.FlowId },
            (
                "test-throw-failstepexception_from_step_variable",
                GenericImplementation.Create(step => throw step.FailAsException("some description"))
            ));

        helper.AssertTableCounts(helper.FlowId, ready: 0, done: 0, failed: 1);
        helper.GetByFlowId(helper.FlowId).Description.Should().Be("some description");
    }

    [Test]
    public async Task When_executing_step_throwing_special_FailCurrentStepException_and_add_step_Then_fail_current_step_and_add_ready_step()
    {
        var name = "test-throw-failstepexception-with-newStep";
        var nameNewStep = "test-throw-failstepexception-with-newStep-newstepname";
        var engine = helper.CreateEngine(
            (
                name,
                GenericImplementation.Create(step => throw step.FailAsException(newSteps: new Step(nameNewStep))))
            );
        await engine.Data.AddStepAsync(new Step(name) { FlowId = helper.FlowId });
        await engine.StartAsSingleWorker(cfg);

        var steps = await engine.Data.SearchStepsAsync(new SearchModel(FlowId: helper.FlowId), FetchLevels.ALL);

        steps.Is(@" [
    {
        Key = Ready
        Value = [
            {
                Id = *
                Name = `test-throw-failstepexception-with-newStep-newstepname`
                Singleton = false
                FlowId = *
                SearchKey = null
                InitialState = null
                State = null
                StateFormat = null
                ActivationArgs = null
                ExecutionCount = *
                ExecutionDurationMillis = null
                ExecutionStartTime = null
                ExecutedBy = null
                CreatedTime = now
                CreatedByStepId = *
                ScheduleTime = *
                CorrelationId = null
                Description = `Worker: missing step-implementation for step 'test-throw-failstepexception-with-newStep-newstepname'`
            }
        ]
    },
    {
        Key = Done
        Value = []
    },
    {
        Key = Failed
        Value = [
            {
                Id = *
                Name = `test-throw-failstepexception-with-newStep`
                Singleton = false
                FlowId = *
                SearchKey = null
                InitialState = null
                State = null
                StateFormat = null
                ActivationArgs = null
                ExecutionCount = 1
                ExecutionDurationMillis = *
                ExecutionStartTime = now
                ExecutedBy = null
                CreatedTime = now
                CreatedByStepId = 0
                ScheduleTime = now
                CorrelationId = null
                Description = `Exception of type 'GreenFeetWorkflow.FailCurrentStepException' was thrown.`
            }
        ]
    }
]");
    }

    [Test]
    public void When_executing_step_throwing_exception_Then_rerun_current_step_and_ensure_state_is_unchanged()
    {
        int? dbid = null;

        helper.CreateAndRunEngine(
            new Step("test-throw-exception", "hej") { FlowId = helper.FlowId },
            (
                "test-throw-exception",
                GenericImplementation.Create(step =>
                {
                    dbid = step.Id;
                    throw new Exception("exception message");
                })));

        helper.AssertTableCounts(helper.FlowId, ready: 1, done: 0, failed: 0);

        var persister = helper.Persister;
        var row = persister.InTransaction(() =>
        persister.SearchSteps(new SearchModel(Id: dbid!.Value), StepStatus.Ready)).Single();
        row!.State.Should().Be("\"hej\"");
        row.FlowId.Should().Be(helper.FlowId);
        row.Name.Should().Be("test-throw-exception");
    }


    [Test]
    public void When_connecting_unsecurely_to_DB_Then_see_the_exception()
    {
        var impl = ("onestep_fails", new GenericImplementation(step => step.Fail()));
        helper.ConnectionString = helper.IllegalConnectionString;
        
        Action act = () => helper.CreateAndRunEngine(new Step("onestep_fails") { FlowId = helper.FlowId }, impl);

        act.Should()
            .Throw<SqlException>()
            .WithMessage("A connection was successfully established with the server, but then an error occurred during the login process.*");
    }

    [Test]
    public void OneStep_fail()
    {
        var impl = ("onestep_fails", new GenericImplementation(step => step.Fail()));

        helper.CreateAndRunEngine(new Step("onestep_fails") { FlowId = helper.FlowId }, impl);

        helper.AssertTableCounts(helper.FlowId, ready: 0, done: 0, failed: 1);
    }

    [Test]
    public void When_executing_step_for_the_first_time_Then_execution_count_is_1()
    {
        int? stepResult = null;

        var impl = (helper.RndName, new GenericImplementation(step =>
        {
            stepResult = step.ExecutionCount;
            return ExecutionResult.Done();
        }));

        helper.CreateAndRunEngine(new[] { new Step(helper.RndName) }, impl);

        stepResult.Should().Be(1);
    }


    [Test]
    public void OneStep_Repeating_Thrice()
    {
        string? stepResult = null;

        var impl = ("OneStep_Repeating_Thrice", new GenericImplementation(step =>
        {
            int counter = helper.Formatter!.Deserialize<int>(step.State);

            stepResult = $"counter {counter} executionCount {step.ExecutionCount}";

            if (counter < 3)
                return ExecutionResult.Rerun(stateForRerun: counter + 1, scheduleTime: step.ScheduleTime);
            return ExecutionResult.Done();
        }));

        helper.CreateAndRunEngine(new Step("OneStep_Repeating_Thrice") { InitialState = 1, FlowId = helper.FlowId }, impl);

        stepResult.Should().Be("counter 3 executionCount 3");

        helper.AssertTableCounts(helper.FlowId, ready: 0, done: 1, failed: 0);
    }

    [Test]
    public void TwoSteps_flow_same_flowid()
    {
        string? stepResult = null;

        var implA = ("check-flowid/a", new GenericImplementation(step => step.Done(new Step("check-flowid/b"))));
        var implB = ("check-flowid/b", new GenericImplementation(step =>
        {
            stepResult = step.FlowId;
            return ExecutionResult.Done();
        }));

        helper.CreateAndRunEngine(new Step { Name = "check-flowid/a", FlowId = helper.FlowId }, implA, implB);

        stepResult.Should().Be($"{helper.FlowId}");

        helper.AssertTableCounts(helper.FlowId, ready: 0, done: 2, failed: 0);
    }

    [Test]
    public void TwoSteps_flow_same_correlationid()
    {
        string? stepResult = null;

        var implA = ("check-correlationid/a", new GenericImplementation(step => step.Done(new Step("check-correlationid/b"))));
        var implB = ("check-correlationid/b", new GenericImplementation(step =>
        {
            stepResult = step.CorrelationId;
            return ExecutionResult.Done();
        }));

        helper.CreateAndRunEngine(new Step
        {
            Name = "check-correlationid/a",
            CorrelationId = helper.CorrelationId,
            FlowId = helper.FlowId,
        }, implA, implB);

        stepResult.Should().Be(helper.CorrelationId);

        helper.AssertTableCounts(helper.FlowId, ready: 0, done: 2, failed: 0);
    }

    [Test]
    public void When_a_step_creates_a_new_step_Then_new_step_may_change_correlationid()
    {
        string? stepResult = null;
        string oldId = Guid.NewGuid().ToString();
        string newId = Guid.NewGuid().ToString();

        var cookHandler = ("check-correlationidchange/cookFood", new GenericImplementation(step =>
        {
            return step.Done()
                .With(new Step("check-correlationidchange/eat")
                {
                    CorrelationId = newId,
                });
        }));
        var eatHandler = ("check-correlationidchange/eat", new GenericImplementation(step =>
        {
            stepResult = step.CorrelationId;
            return step.Done();
        }));

        helper.CreateAndRunEngine(new[] { new Step()
            {
                Name = "check-correlationidchange/cookFood",
                CorrelationId = oldId
            } },
            cookHandler, eatHandler);

        stepResult.Should().Be(newId);
    }

    [Test]
    public void TwoSteps_flow_last_step_starting_in_the_future_so_test_terminate_before_its_executions()
    {
        string? stepResult = null;


        helper.CreateAndRunEngine(
            new[] { new Step() { Name = "check-future-step/cookFood", FlowId = helper.FlowId } },
            ("check-future-step/cookFood",
            step =>
            {
                string food = "potatoes";
                stepResult = $"cooking {food}";
                return ExecutionResult.Done(
                    new Step("check-future-step/eat", food) { ScheduleTime = DateTime.Now.AddYears(30) });
            }
        ),
            ("check-future-step/eat",
            step =>
            {
                var food = helper.Formatter!.Deserialize<string>(step.State);
                stepResult = $"eating {food}";
                return ExecutionResult.Done();
            }
        ));

        stepResult.Should().Be($"cooking potatoes");

        helper.AssertTableCounts(helper.FlowId, ready: 1, done: 1, failed: 0);
    }


    [Test]
    public void When_step_is_in_the_future_Then_it_wont_execute()
    {
        string? stepResult = null;

        var name = "When_step_is_in_the_future_Then_it_wont_execute";

        var engine = helper.CreateEngine((name, GenericImplementation.Create(step => { stepResult = step.FlowId; return step.Done(); })));
        Step futureStep = new()
        {
            Name = name,
            FlowId = helper.FlowId,
            ScheduleTime = DateTime.Now.AddYears(35)
        };
        var id = engine.Data.AddStepAsync(futureStep, null);
        engine.Start(cfg);
        helper.AssertTableCounts(helper.FlowId, ready: 1, done: 0, failed: 0);

        stepResult.Should().BeNull();
    }

    [Test]
    public async Task When_step_is_in_the_future_Then_it_can_be_activated_to_execute_now()
    {
        string? stepResult = null;

        var name = "When_step_is_in_the_future_Then_it_can_be_activated_to_execute_now";

        var engine = helper.CreateEngine((name, GenericImplementation.Create(step => { stepResult = step.FlowId; return step.Done(); })));
        Step futureStep = new()
        {
            Name = name,
            FlowId = helper.FlowId,
            ScheduleTime = DateTime.Now.AddYears(35)
        };
        var id = await engine.Data.AddStepAsync(futureStep, null);
        var count = await engine.Data.ActivateStepAsync(id, null);
        count.Should().Be(1);

        engine.Start(cfg);

        stepResult.Should().Be(helper.FlowId.ToString());

        helper.AssertTableCounts(helper.FlowId, ready: 0, done: 1, failed: 0);
    }

    [Test]
    public async Task When_step_is_in_the_future_Then_it_can_be_activated_to_execute_now_with_args()
    {
        string? stepResult = null;
        string args = "1234";
        var name = "When_step_is_in_the_future_Then_it_can_be_activated_to_execute_now_with_args";

        var engine = helper.CreateEngine((name, GenericImplementation.Create(step => { stepResult = step.ActivationArgs; return step.Done(); })));
        Step futureStep = new()
        {
            Name = name,
            FlowId = helper.FlowId,
            ScheduleTime = DateTime.Now.AddYears(35)
        };
        var id = await engine.Data.AddStepAsync(futureStep, null);
        var count = await engine.Data.ActivateStepAsync(id, args);
        count.Should().Be(1);
        engine.Start(cfg);

        stepResult.Should().Be(JsonConvert.SerializeObject(args));
        helper.AssertTableCounts(helper.FlowId, ready: 0, done: 1, failed: 0);
    }


    [Test]
    public void TwoSteps_flow_with_last_step_undefined_stephandler__so_test_terminate()
    {
        string? stepResult = null;

        var cookHandler = ("undefined-next-step/cookFood", new GenericImplementation((step) =>
        {
            stepResult = $"cooking {"potatoes"}";
            return step.Done(new Step("undefined-next-step/eat", "potatoes"));
        }));

        helper.CreateAndRunEngine(
            new[] { new Step() { Name = "undefined-next-step/cookFood", FlowId = helper.FlowId } },
            cookHandler);

        stepResult.Should().Be($"cooking potatoes");

        helper.AssertTableCounts(helper.FlowId, ready: 1, done: 1, failed: 0);
    }


    [Test]
    public void When_a_step_creates_two_steps_Then_those_steps_can_be_synchronized_and_join_into_a_forth_merge_step()
    {
        string? stepResult = null;

        var stepDriveToShop = new Step("v1/forkjoin/drive-to-shop", new[] { "milk", "cookies" });
        var payForStuff = new Step("v1/forkjoin/pay");

        var drive = ("v1/forkjoin/drive-to-shop", GenericImplementation.Create(step =>
        {
            stepResult = $"driving";
            var id = Guid.NewGuid();
            Step milk = new("v1/forkjoin/pick-milk", new BuyInstructions() { Item = "milk", Count = 1, PurchaseId = id });
            Step cookies = new("v1/forkjoin/pick-cookies", new BuyInstructions() { Item = "cookies", Count = 30, PurchaseId = id });
            Step pay = new("v1/forkjoin/pay-for-all", (count: 2, id, maxWait: DateTime.Now.AddSeconds(8)));
            return step.Done(milk, cookies, pay);
        }));

        var checkout = ("v1/forkjoin/pay-for-all", GenericImplementation.Create(step =>
        {
            (int count, Guid id, DateTime maxWait) = helper.Formatter!.Deserialize<(int, Guid, DateTime)>(step.State);
            var sales = GroceryBuyer.SalesDb.Where(x => x.id == id).ToArray();
            if (sales.Length != 2 && DateTime.Now <= maxWait)
                return ExecutionResult.Rerun(scheduleTime: DateTime.Now.AddSeconds(0.2));

            stepResult = $"total: {sales.Sum(x => x.total)}";
            helper.cts.Cancel();
            return ExecutionResult.Done();
        }));

        helper.CreateAndRunEngine(
            new[] { new Step() { Name = "v1/forkjoin/drive-to-shop", FlowId = helper.FlowId } },
            4,
            drive,
            checkout,
            ("v1/forkjoin/pick-milk", new GroceryBuyer()),
            ("v1/forkjoin/pick-cookies", new GroceryBuyer()));

        stepResult.Should().Be($"total: 61");

        helper.AssertTableCounts(helper.FlowId, ready: 0, done: 4, failed: 0);
    }


    class BuyInstructions
    {
        public Guid PurchaseId { get; set; }
        public string? Item { get; set; }
        public int Count { get; set; }
    }

    class GroceryBuyer : IStepImplementation
    {
        internal static readonly List<(Guid id, string name, int total)> SalesDb = new();
        static readonly Dictionary<string, int> prices = new() { { "milk", 1 }, { "cookies", 2 } };

        public async Task<ExecutionResult> ExecuteAsync(Step step)
        {
            Debug.WriteLine("Picking up stuff");
            Thread.Sleep(100);

            var instruction = JsonConvert.DeserializeObject<BuyInstructions>(step.State!);

            lock (SalesDb)
            {
                SalesDb.Add((instruction!.PurchaseId, instruction.Item!, prices[instruction.Item!] * instruction.Count));
            }

            return await Task.FromResult(step.Done());
        }
    }
}