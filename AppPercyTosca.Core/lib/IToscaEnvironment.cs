namespace AppPercyTosca.Core
{
    /// <summary>
    /// Everything this SDK needs from Tosca, expressed without any Tricentis type.
    ///
    /// It is this small because there is no driver to borrow: Mobile Engine 3.0 runs out of process,
    /// and <c>ISpecialExecutionTaskTestAction</c> is only result-reporting and parameter access. So
    /// Tosca supplies the hub address and the session id, and everything else goes to the session
    /// directly over HTTP.
    ///
    /// The shim implements this over the Tricentis APIs; tests implement it directly, which is what
    /// keeps <see cref="ToscaMobileDriver"/> verifiable with no Tosca installed.
    /// </summary>
    public interface IToscaEnvironment
    {
        /// <summary>
        /// A test configuration parameter by name, or null. This is where the mobile engine's
        /// connection details live, <c>AppiumServer</c> above all.
        /// </summary>
        string? TestConfigurationParameter(string name);

        /// <summary>Every TCP for the run, carried through as capabilities the session did not report.</summary>
        IReadOnlyDictionary<string, string?> TestConfigurationParameters();

        /// <summary>
        /// A Tosca buffer by name, or null. How the Appium session id reaches the extension when the
        /// step does not pass it: the <c>Get Appium Session Id</c> module writes it to one.
        /// </summary>
        string? Buffer(string name);
    }
}
