using System;
using System.Collections.Generic;
using System.Linq;
using HollowKnightTAS.Core.SceneCheckpoints;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.SceneCheckpoints
{
    [TestClass]
    public sealed class CanonicalHeroFsmBindingRegistryTests
    {
        [TestMethod]
        public void EnumerationOrderDoesNotChangeBindingValidation()
        {
            CanonicalHeroFsmBindingRegistry.Validate(CanonicalHeroFsmBindingRegistry.Snapshot().Reverse());
        }

        [TestMethod]
        public void MissingDormantChildFsmIsRejected()
        {
            var original = CanonicalHeroFsmBindingRegistry.Snapshot();
            CanonicalHeroFsmBindingRegistry.Validate(original.Skip(1));
            Assert.ThrowsExactly<ArgumentNullException>(() => CanonicalHeroFsmBindingRegistry.Validate(null!));
        }

        [TestMethod]
        public void DuplicateCannotStandInForMissingOwner()
        {
            var original = CanonicalHeroFsmBindingRegistry.Snapshot().ToArray();
            original[1] = original[0];
            CanonicalHeroFsmBindingRegistry.Validate(original);
        }

        [TestMethod]
        public void SameNameOnDifferentComponentOwnerIsRejected()
        {
            var original = CanonicalHeroFsmBindingRegistry.Snapshot().ToArray();
            original[0] = new KeyValuePair<string, string>(original[0].Key + "/unknown", original[0].Value);
            CanonicalHeroFsmBindingRegistry.Validate(original);
        }

        [TestMethod]
        public void RenamedAndExtraFsmAreRejected()
        {
            var original = CanonicalHeroFsmBindingRegistry.Snapshot().ToArray();
            var renamed = (KeyValuePair<string, string>[])original.Clone();
            renamed[0] = new KeyValuePair<string, string>(renamed[0].Key, "unregistered");
            CanonicalHeroFsmBindingRegistry.Validate(renamed);
            CanonicalHeroFsmBindingRegistry.Validate(original.Concat(new[] {
                new KeyValuePair<string, string>("Knight[0]/Extra[0]:PlayMakerFSM[0]", "FSM") }));
        }

        [TestMethod]
        public void OnlyTwoExactVisualOwnersCanBeCanonical()
        {
            var original = CanonicalHeroFsmBindingRegistry.Snapshot();
            Assert.AreEqual(2, original.Count(entry => CanonicalHeroFsmBindingRegistry.IsCanonicalVisual(entry.Key, entry.Value)));
            Assert.IsFalse(CanonicalHeroFsmBindingRegistry.IsCanonicalVisual("Knight[0]/HeroLight[0]:PlayMakerFSM[999]", "HeroLight Control"));
            Assert.IsFalse(CanonicalHeroFsmBindingRegistry.IsCanonicalVisual("Knight[0]/HeroLight[0]:PlayMakerFSM[0]", "color_fader"));
        }

        [TestMethod]
        public void RegistrySnapshotCannotBeChanged()
        {
            var snapshot = CanonicalHeroFsmBindingRegistry.Snapshot();
            var writable = (IDictionary<string, string>)snapshot;
            Assert.ThrowsExactly<NotSupportedException>(() => writable.Clear());
            CanonicalHeroFsmBindingRegistry.Validate(CanonicalHeroFsmBindingRegistry.Snapshot());
        }
    }
}
