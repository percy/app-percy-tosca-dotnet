### Steps

The DLL is built by hand and committed, because it can only be built on a machine with Tosca
Testsuite installed — a GitHub runner has no Tricentis assemblies to reference. The release workflow
uploads whatever is committed, so the build has to happen before the tag.

1. Bump the version in **both** places, or Percy will record an SDK version that does not match the
   assembly: `AppPercyTosca/Properties/AssemblyInfo.cs` and `Env.SdkVersion` in
   `AppPercyTosca.Core/Env.cs`.
2. On a machine with Tosca Commander 24, build the extension and copy the output over the committed
   DLL:
   ```
   dotnet build dotnet-8/AppPercyTosca.sln -c Release
   cp ./dotnet-8/AppPercyTosca/bin/Release/net8.0/AppPercyTosca.dll ./dotnet-8/AppPercyTosca_v8.dll
   ```
   The Core is compiled into that assembly, so `AppPercyTosca_v8.dll` is the whole extension — there
   is no second DLL to ship.
3. Sanity-check it in Tosca: drop it in the extension folder, restart Commander, and run a
   PercyScreenshot step with `Diagnose` set to `true`. CI cannot cover this step, and it is the one
   that catches a Tricentis signature having changed.
4. Commit the DLL, merge, then publish a GitHub release. The `Upload DLL` workflow attaches it.
