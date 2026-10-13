using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace WorkLedger
{
    public static class UiPreferencesTests
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("UI preferences test: " + message);
        }
        private static void AssertDefaults(UiPreferences preferences, string message)
        {
            Assert(preferences.HiddenSections.Count == 0 && preferences.AnimationsEnabled
                && UiPreferences.Sections.All(section => preferences.IsVisible(section.Key)), message);
        }
        public static void Run(string rootPath)
        {
            Directory.CreateDirectory(rootPath);
            string dataDir = Path.Combine(rootPath, "shared-database");
            var store = new Store(dataDir);
            if (!store.Of("party").Any())
            {
                var party = new Record("party"); party.Set("name", "Контрагент для проверки настроек"); party.Set("role", "client"); store.Upsert(party);
            }
            byte[] ledgerBefore = File.ReadAllBytes(store.DataPath);
            long revisionBefore = store.Revision;
            string identityA = "test-profile-A", identityB = "test-profile-B";
            string profileA = UiPreferences.GetProfilePath(dataDir, identityA);
            string profileB = UiPreferences.GetProfilePath(dataDir, identityB);
            Assert(profileA != profileB && Path.GetDirectoryName(profileA) == Path.Combine(Path.GetFullPath(dataDir), "ui-preferences"), "profiles are not isolated within the database folder");

            string missingIdentity = "never-saved-" + Guid.NewGuid().ToString("N");
            AssertDefaults(UiPreferences.Load(dataDir, missingIdentity), "missing profile did not restore all visible defaults");
            Assert(!File.Exists(UiPreferences.GetProfilePath(dataDir, missingIdentity)), "reading missing settings wrote a profile");
            string[] expected = { "overview", "create-act", "party", "project", "contract", "amendment", "estimate", "work", "act", "external-act", "invoice", "payment", "gray-payment", "taxes", "expense", "hold", "document", "reports", "audit", "updates" };
            Assert(expected.SequenceEqual(UiPreferences.Sections.Select(section => section.Key)), "navigation section metadata is missing or reordered");

            var first = new UiPreferences { AnimationsEnabled = false };
            foreach (UiSection section in UiPreferences.Sections) first.SetVisible(section.Key, section.Key == "create-act" || section.Key == "party" || section.Key == "act");
            first.Save(dataDir, identityA);
            UiPreferences restored = UiPreferences.Load(dataDir, identityA);
            Assert(!restored.AnimationsEnabled && first.HiddenSections.SetEquals(restored.HiddenSections), "hidden section keys or animation choice did not round-trip");
            var second = new UiPreferences(); second.SetVisible("audit", false); second.Save(dataDir, identityB);
            Assert(UiPreferences.Load(dataDir, identityB).HiddenSections.SetEquals(new[] { "audit" })
                && UiPreferences.Load(dataDir, identityB).AnimationsEnabled, "second profile did not preserve independent settings");
            Assert(first.HiddenSections.SetEquals(UiPreferences.Load(dataDir, identityA).HiddenSections)
                && !UiPreferences.Load(dataDir, identityA).AnimationsEnabled, "saving another Windows user changed the first user profile");
            UiPreferences copy = first.Clone(); copy.SetVisible("overview", true); copy.AnimationsEnabled = true;
            Assert(!first.IsVisible("overview") && !first.AnimationsEnabled, "editing a copy changed active preferences before applying");

            byte[] validProfile = File.ReadAllBytes(profileA);
            var allHidden = new UiPreferences();
            foreach (UiSection section in UiPreferences.Sections) allHidden.SetVisible(section.Key, false);
            bool rejected = false;
            try { allHidden.Save(dataDir, identityA); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && validProfile.SequenceEqual(File.ReadAllBytes(profileA)), "empty menu overwrote valid preferences");
            allHidden.SetVisible("updates", true); rejected = false;
            try { allHidden.Save(dataDir, identityA); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && validProfile.SequenceEqual(File.ReadAllBytes(profileA)), "updates alone was accepted as a usable menu");
            allHidden.SetVisible("act", true); allHidden.Save(dataDir, identityA);
            Assert(UiPreferences.Load(dataDir, identityA).IsVisible("act")
                && UiPreferences.Load(dataDir, identityA).HiddenSections.Count == UiPreferences.Sections.Count - 2, "one main section plus updates was rejected");

            string[] malformed =
            {
                "{broken json",
                "null",
                "{}",
                "{\"Version\":2,\"AnimationsEnabled\":false,\"HiddenSections\":[]}",
                "{\"Version\":1,\"HiddenSections\":[]}",
                "{\"Version\":1,\"AnimationsEnabled\":\"yes\",\"HiddenSections\":[]}",
                "{\"Version\":1,\"AnimationsEnabled\":false,\"HiddenSections\":null}",
                new JavaScriptSerializer().Serialize(new { Version = 1, AnimationsEnabled = false, HiddenSections = expected }),
                new String('x', 32769)
            };
            foreach (string invalid in malformed)
            {
                File.WriteAllText(profileA, invalid, Encoding.UTF8);
                AssertDefaults(UiPreferences.Load(dataDir, identityA), "malformed settings prevented a safe full menu");
            }
            File.WriteAllText(profileA, "{\"Version\":1,\"AnimationsEnabled\":false,\"HiddenSections\":[\"expense\",\"expense\",\"future-section\"]}", Encoding.UTF8);
            UiPreferences normalized = UiPreferences.Load(dataDir, identityA);
            Assert(!normalized.AnimationsEnabled && normalized.HiddenSections.SetEquals(new[] { "expense" }), "duplicate and future section keys were not normalized");
            first.Save(dataDir, identityA);
            Assert(!Directory.GetFiles(Path.GetDirectoryName(profileA), "*.tmp").Any(), "atomic saving left temporary settings files");

            using (var dialog = new InterfaceSettingsDialog(dataDir, first))
            {
                dialog.SelectPreset("acts"); UiPreferences acts = dialog.GetSelectedPreferences();
                Assert(UiPreferences.Sections.Where(section => acts.IsVisible(section.Key)).Select(section => section.Key)
                    .SequenceEqual(new[] { "create-act", "party", "act" }), "acts preset does not leave a complete acts workflow");
                Assert(!acts.AnimationsEnabled && dialog.ResultPreferences == null, "selecting a preset changed animations or saved before confirmation");
                dialog.SelectPreset("acts-payments"); UiPreferences payments = dialog.GetSelectedPreferences();
                Assert(payments.IsVisible("create-act") && payments.IsVisible("act") && payments.IsVisible("party")
                    && payments.IsVisible("payment") && payments.IsVisible("invoice") && payments.IsVisible("taxes") && payments.IsVisible(Catalog.GrayPaymentSection) && payments.IsVisible("reports"), "acts and payments preset lacks its required sections");
                dialog.SelectPreset("all"); Assert(dialog.GetSelectedPreferences().HiddenSections.Count == 0, "all-sections preset failed to restore a hidden tab");
            }
            Assert(!first.IsVisible("overview") && !first.AnimationsEnabled, "closing interface dialog changed active settings");
            Assert(store.Revision == revisionBefore && ledgerBefore.SequenceEqual(File.ReadAllBytes(store.DataPath)), "interface preferences changed accounting data");
            Console.WriteLine("PASS: personal UI preferences, isolated users, recovery, atomic persistence and presets");
        }
    }
}