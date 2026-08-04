using Tricentis.Automation.AutomationInstructions.TestActions;
using Tricentis.Automation.Engines;

namespace AppPercyTosca
{
    /// <summary>
    /// Wraps the executing test action so the mobile engine's screenshot task can be handed a
    /// destination the Percy module does not declare.
    ///
    /// That task reads <c>Directory</c> and <c>Filename</c> off whatever test action it is given —
    /// it was written for Tosca's own PrintScreen module, which has those rows. A Percy module does
    /// not, and requiring users to add them would put a temp path in every test sheet. So this
    /// answers those two parameter names itself and delegates the rest to the real action, which is
    /// what keeps the file destination an implementation detail.
    ///
    /// Everything besides parameter reading is forwarded, so results the task reports still land on
    /// the real action.
    /// </summary>
    internal class ScreenshotTestAction : ISpecialExecutionTaskTestAction
    {
        private readonly ISpecialExecutionTaskTestAction _inner;
        private readonly string _directory;
        private readonly string _fileName;

        internal ScreenshotTestAction(
            ISpecialExecutionTaskTestAction inner, string directory, string fileName)
        {
            _inner = inner;
            _directory = directory;
            _fileName = fileName;
        }

        public IInputValue? GetParameterAsInputValue(string name, bool optional)
        {
            switch (name)
            {
                case "Directory":
                    return new StaticInputValue(_directory);
                case "Filename":
                case "FileName":
                    return new StaticInputValue(_fileName);
                default:
                    // Notably includes "Environment", which the task uses to pick where to capture
                    // from — that has to come from the real action, not from here.
                    return _inner.GetParameterAsInputValue(name, optional);
            }
        }

        public void SetResult(ActionResult result) => _inner.SetResult(result);

        /// <summary>An <see cref="IInputValue"/> over a value this SDK supplies rather than Tosca.</summary>
        private class StaticInputValue : IInputValue
        {
            internal StaticInputValue(string value) => Value = value;

            public object? Value { get; }
        }
    }
}
