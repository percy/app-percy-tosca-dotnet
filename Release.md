### Steps

The DLL is built by hand and committed, because it can only be built on a machine with Tosca
Testsuite installed — a GitHub runner has no Tricentis assemblies to reference. The release workflow
uploads whatever is committed, so the build has to happen before the tag.

1. Bump the version in **both** places, or Percy will record an SDK version that does not match the
   assembly: `<Version>` in `AppPercyTosca/AppPercyTosca.csproj` and `Env.SdkVersion` in
   `AppPercyTosca.Core/Env.cs`.
2. On a machine with Tosca Commander 24, build the extension and copy the output over the committed
   DLL:
   ```
   dotnet build AppPercyTosca.sln -c Release
   cp ./AppPercyTosca/bin/Release/net8.0/AppPercyTosca.dll ./AppPercyTosca_v8.dll
   ```
   The Core is compiled into that assembly, so `AppPercyTosca_v8.dll` is the whole extension — there
   is no second DLL to ship.
3. Sanity-check it in Tosca: drop it in the extension folder, restart Commander, and run a
   AppPercyScreenshot step against a real device, with `PERCY_LOGLEVEL=debug` set. CI cannot cover this
   step, and it is the one that catches a Tricentis signature having changed.
4. **Export the module as a Tosca subset** and commit it as `AppPercyScreenshot.tsu` in the repository
   root, the way the web SDK ships `PercySnapshot.tsu`. Do this from the module you just verified in
   step 3, so what ships is a configuration known to work.

   This matters more than it looks. Every module row is typed by hand today, and a mistyped task name
   or engine reports as `The SpecialExecutionTask 'x' was not found for engine 'y'` — indistinguishable
   from a broken install. A subset removes that whole class of problem. Add it to `release.yml`'s
   `files:` list once it exists, and drop the "Not yet shipped" note from the Readme.
5. Commit the DLL, merge, then publish a GitHub release. The `Upload DLL` workflow attaches it.
