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
    /// The shim implements this over those Tricentis APIs; tests implement it directly, which is
    /// what keeps <see cref="ToscaMobileDriver"/> — the part with the actual decisions in it —
    /// verifiable on a machine with no Tosca installed.
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
        /// Every TCP passed for the run. Used to carry the whole set through to Percy on Automate as
        /// capabilities, since the CLI knows how to interpret more of them than this SDK does.
        /// </summary>
        IReadOnlyDictionary<string, string?> TestConfigurationParameters();

        /// <summary>
        /// A Tosca buffer by name, or null when unset. This is how the Appium session id reaches
        /// the extension: the <c>Get Appium Session Id</c> standard module writes it to a buffer,
        /// and Percy on Automate cannot work without it.
        /// </summary>
        string? Buffer(string name);

        /// <summary>
        /// Captures the device screen to a PNG and returns its full path, or null when the mobile
        /// engine would not produce one.
        ///
        /// Implemented by delegating to the mobile engine's own <c>Mobile30PrintScreen</c> task — the
        /// same route Tosca's built-in PrintScreen uses for mobile — because there is no in-process
        /// driver to ask directly.
        ///
        /// The destination is deliberately not a parameter here. That task reads its own
        /// <c>Directory</c> and <c>Filename</c> from the test action it is handed, so on Tosca the
        /// path is decided by the module rather than chosen by this SDK, and only the shim can know
        /// it. The caller just wants the bytes.
        /// </summary>
        string? CaptureScreenshot();

        /// <summary>
        /// Whether raw Appium commands can be sent through this session. False on Tosca: the
        /// <c>Execute Driver Script</c> module is restricted to Tricentis' own device cloud, so the
        /// BrowserStack <c>browserstack_executor</c> commands that drive App Automate's remote
        /// capture are not reachable. Kept as a property rather than hard-coded false so a Tosca
        /// release that opens this up needs no change in the Core.
        /// </summary>
        bool CanExecuteScript { get; }

        /// <summary>
        /// Runs a raw automation command. Only called when <see cref="CanExecuteScript"/> is true.
        /// </summary>
        string? ExecuteScript(string script);
    }
}
