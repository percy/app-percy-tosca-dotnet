namespace AppPercyTosca.Core
{
    /// <summary>
    /// The four things this SDK needs from Tosca, expressed without any Tricentis type.
    ///
    /// Mobile Engine 3.0 runs out-of-process — Tosca Commander talks to a separate mobile server
    /// over IPC — so there is no Appium driver object living in the extension's process to borrow,
    /// and <c>ISpecialExecutionTaskTestAction</c> exposes no route to the device either (its whole
    /// surface is result-reporting and parameter access). Everything therefore comes through the
    /// documented seams instead: test configuration parameters, Tosca buffers, and the mobile
    /// engine's own screenshot task.
    ///
    /// Two members, not four: capturing and scripting used to be here as well, delegated to Tosca's own
    /// screenshot task. Both now go straight to the session over HTTP, which needs nothing from Tosca
    /// beyond the address and the id below.
    ///
    /// The shim implements this over those Tricentis APIs; tests implement it directly, which is what
    /// keeps <see cref="ToscaMobileDriver"/> — the part with the actual decisions in it — verifiable on
    /// a machine with no Tosca installed.
    /// </summary>
    public interface IToscaEnvironment
    {
        /// <summary>
        /// A test configuration parameter (TCP) by name, or null when it is not set. TCPs are where
        /// the mobile engine's connection details live: <c>AppiumServer</c> is the hub URL,
        /// alongside <c>DeviceName</c>, <c>OSVersion</c> and friends.
        /// </summary>
        string? TestConfigurationParameter(string name);

        /// <summary>
        /// Every TCP passed for the run. Carried through as the session's capabilities, which is where
        /// the device name, OS and version come from.
        /// </summary>
        IReadOnlyDictionary<string, string?> TestConfigurationParameters();

        /// <summary>
        /// A Tosca buffer by name, or null when unset. This is how the Appium session id reaches the
        /// extension: the <c>Get Appium Session Id</c> standard module writes it to a buffer. Capture
        /// asks the device session for the screen directly using it, so it is the one thing worth
        /// setting up before anything else.
        /// </summary>
        string? Buffer(string name);
    }
}
