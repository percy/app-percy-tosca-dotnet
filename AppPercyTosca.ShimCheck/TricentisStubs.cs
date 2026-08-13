// Minimal stand-ins for the Tricentis types the shim uses, with the same names, namespaces and
// signatures as the ones percy/percy-tosca-dotnet compiles against on Tosca Commander 24.
// Used only to compile-check the shim on a machine without Tosca installed. Not shipped.
namespace Tricentis.Automation.Creation
{
    public class Validator { }
}

namespace Tricentis.Automation.Creation.Attributes
{
    [System.AttributeUsage(System.AttributeTargets.Assembly)]
    public class EngineIdAttribute : System.Attribute
    {
        public EngineIdAttribute(string id) { }
    }
}

namespace Tricentis.Automation.Engines
{
    public abstract class ActionResult { }

    public class PassedActionResult : ActionResult
    {
        public PassedActionResult(string message) { }
    }

    public class UnknownFailedActionResult : ActionResult
    {
        public UnknownFailedActionResult(string message) { }
    }
}

namespace Tricentis.Automation.AutomationInstructions.TestActions
{
    public interface IInputValue
    {
        object? Value { get; }
    }

    // Only what the shim calls is stubbed, and that is the point: the shim must consume this interface,
    // never implement it. The real one has ~35 members — all of ITestAction plus a dozen
    // result-reporting overloads — so implementing it means 34 CS0535 errors against the real Tosca,
    // which no runner can catch.
    public interface ISpecialExecutionTaskTestAction
    {
        IInputValue? GetParameterAsInputValue(string name, bool optional);
    }
}

namespace Tricentis.Automation.Engines.SpecialExecutionTasks
{
    using Tricentis.Automation.AutomationInstructions.TestActions;

    public abstract class SpecialExecutionTask
    {
        protected SpecialExecutionTask(Tricentis.Automation.Creation.Validator validator) { }

        public abstract ActionResult Execute(ISpecialExecutionTaskTestAction testAction);
    }

    public interface ISpecialExecutionTask
    {
        void ExecuteTask(ISpecialExecutionTaskTestAction testAction);
    }

    // Tricentis' own PrintScreen source calls this to delegate mobile capture to the mobile engine:
    //   SpecialExecutionTaskFactory.CreateTask("Mobile30PrintScreen", "ME3.0")
    public static class SpecialExecutionTaskFactory
    {
        public static ISpecialExecutionTask CreateTask(string specialExecutionTaskName, string engineId) =>
            throw new System.NotImplementedException("stub");
    }
}

namespace Tricentis.Automation.Engines.SpecialExecutionTasks.Attributes
{
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public class SpecialExecutionTaskNameAttribute : System.Attribute
    {
        public SpecialExecutionTaskNameAttribute(string name) { }
    }
}
