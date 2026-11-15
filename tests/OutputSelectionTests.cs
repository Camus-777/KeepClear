using System;

namespace KeepClear
{
    internal static class OutputSelectionTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var driver = DriverChecks.Node("cable");
            var standard = new AudioDevice { Id = "standard", Name = "User-renamed regular endpoint", FormFactor = 1, Driver = driver };
            var multi = new AudioDevice { Id = "multi", Name = "CABLE Input", FormFactor = 2, Driver = driver };
            var both = new[] { multi, standard };
            check(AudioStartupTest.ChooseOutput(both, "") == standard, "first use prefers the regular speaker endpoint despite reversed order or renamed devices");
            check(AudioStartupTest.ChooseOutput(both, "multi") == multi, "explicit saved multichannel selection is preserved");
            check(AudioStartupTest.ChooseOutput(both, "missing") == standard, "a missing saved output falls back to regular CABLE Input");
            check(AudioStartupTest.ChooseOutput(new[] { multi }, "") == multi, "a sole verified multichannel endpoint remains usable");
            var physical = new AudioDevice { Id = "physical", Name = "CABLE Input", FormFactor = 1 };
            var capture = new AudioDevice { Id = "capture", Name = "CABLE Input", FormFactor = 1, Capture = true, Driver = driver };
            check(AudioStartupTest.ChooseOutput(new[] { physical, capture }, "physical") == null, "display names cannot promote unrelated hardware or capture endpoints to virtual outputs");
            var unknownMulti = new AudioDevice { Id = "unknown-multi", Name = "CABLE In 16ch (VB-Audio Virtual Cable)", Driver = driver };
            var unknownStandard = new AudioDevice { Id = "unknown-standard", Name = "CABLE Input (VB-Audio Virtual Cable)", Driver = driver };
            check(AudioStartupTest.ChooseOutput(new[] { unknownMulti, unknownStandard }, "") == unknownStandard, "missing form-factor metadata still prefers the regular label after driver verification");
            unknownStandard.Name = "Custom output";
            check(AudioStartupTest.ChooseOutput(new[] { unknownStandard }, "") == unknownStandard, "unknown optional metadata and a custom label cannot reject a valid sole output");
            standard.IdentityError = "endpoint unavailable";
            check(AudioStartupTest.ChooseOutput(both, "standard") == multi, "an output with an identity or state error is not preferred or restored");
        }
    }
}
