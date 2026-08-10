namespace AppPercyTosca.Core
{
    /// Everything this SDK needs from Tosca, expressed without any Tricentis type.
    ///
    /// It is this small because there is no driver to borrow: Mobile Engine 3.0 runs out of process,
    /// and <c>ISpecialExecutionTaskTestAction</c> is only result-reporting and parameter access. So
    /// Tosca supplies the hub address and the session id, and everything else goes to the session
    /// directly over HTTP.
    ///
    /// The shim implements this over the Tricentis APIs; tests implement it directly, which is what
    /// keeps <see cref="ToscaMobileDriver"/> verifiable with no Tosca installed.
    public interface IToscaEnvironment
    {
        /// A test configuration parameter by name, or null. This is where the mobile engine's
        /// connection details live, <c>AppiumServer</c> above all.
        string? TestConfigurationParameter(string name);

        /// Every TCP for the run, carried through as capabilities the session did not report.
        IReadOnlyDictionary<string, string?> TestConfigurationParameters();

        /// A Tosca buffer by name, or null. How the Appium session id reaches the extension when the
        /// step does not pass it: the <c>Get Appium Session Id</c> module writes it to one.
        string? Buffer(string name);
    }
}
