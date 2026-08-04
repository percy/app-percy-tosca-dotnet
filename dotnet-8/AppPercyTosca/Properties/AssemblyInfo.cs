using System.Reflection;
using System.Runtime.InteropServices;

// Keep the version in step with Env.SdkVersion in the Core — that constant is what Percy records as
// clientInfo for every snapshot, so a mismatch makes a build's SDK version unverifiable.
[assembly: AssemblyTitle("AppPercyTosca")]
[assembly: AssemblyDescription("App Percy visual testing for Tricentis Tosca mobile tests")]
[assembly: AssemblyCompany("Perceptual Inc")]
[assembly: AssemblyProduct("AppPercyTosca")]
[assembly: AssemblyCopyright("Copyright Perceptual Inc")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: ComVisible(false)]
