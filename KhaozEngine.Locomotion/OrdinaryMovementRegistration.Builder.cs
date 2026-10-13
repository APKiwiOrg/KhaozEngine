using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

public sealed partial class OrdinaryMovementRegistration
{
    public sealed class Builder
    {
        readonly List<Entry> _entries = [];
        readonly HashSet<StaticHandle> _excluded = [];
        internal string WorldId { get; }
        internal IPhysicsWorld Source { get; }
        internal OrdinaryMovementExtent Extent { get; }

        internal Builder(string worldId, IPhysicsWorld source, OrdinaryMovementExtent extent)
        {
            MovementEnvironmentValidation.RequireName(worldId, nameof(worldId));
            ArgumentNullException.ThrowIfNull(source);
            _ = new OrdinaryMovementExtent(extent.Minimum, extent.Maximum, extent.AnalyticMinimumY, extent.AnalyticMaximumY);
            WorldId = worldId;
            Source = source;
            Extent = extent;
        }

        public Builder Add(StaticHandle handle, int firstFeatureId, int lastFeatureId,
            MovementSupportKey owner, Vector3 absoluteMinimum, Vector3 absoluteMaximum)
        {
            MovementEnvironmentValidation.Require(handle.Value >= 0 && firstFeatureId >= 0 &&
                lastFeatureId >= firstFeatureId, nameof(handle));
            MovementEnvironmentValidation.Require(owner.IsValid && owner.WorldId == WorldId, nameof(owner));
            OrdinaryMovementExtent.ValidateBounds(absoluteMinimum, absoluteMaximum);
            _entries.Add(new(handle, firstFeatureId, lastFeatureId, owner, absoluteMinimum, absoluteMaximum));
            return this;
        }

        public Builder Exclude(StaticHandle handle)
        {
            MovementEnvironmentValidation.Require(handle.Value >= 0, nameof(handle));
            _excluded.Add(handle);
            return this;
        }

        public OrdinaryMovementRegistration Build(IPhysicsQueryLease physicsLease)
        {
            ArgumentNullException.ThrowIfNull(physicsLease);
            physicsLease.AssertCurrent();
            if (!ReferenceEquals(physicsLease.SourceWorld, Source))
                throw new ArgumentException("The registration requires its source world's read lease.", nameof(physicsLease));
            foreach (StaticHandle excluded in _excluded)
                MovementEnvironmentValidation.Require(_entries.Any(row => row.Handle == excluded), nameof(_excluded));
            foreach (Entry row in _entries)
                MovementEnvironmentValidation.Require(row.Minimum.X >= Extent.Minimum.X && row.Minimum.Y >= Extent.Minimum.Y &&
                    row.Minimum.Z >= Extent.Minimum.Z && row.Maximum.X <= Extent.Maximum.X &&
                    row.Maximum.Y <= Extent.Maximum.Y && row.Maximum.Z <= Extent.Maximum.Z, nameof(Extent));
            var snapshot = _entries.Distinct().GroupBy(row => row.Handle).ToDictionary(group => group.Key, group => group.ToArray());
            StaticHandle[] exclusions = _excluded.OrderBy(handle => handle.Value).ToArray();
            // Local handles never enter portable compatibility. Retain excluded provenance in the digest.
            string[] provenance = _entries.Select(row => string.Join("/", row.Owner.WorldId.Length, row.Owner.WorldId,
                row.Owner.LocalId.Length, row.Owner.LocalId, row.First, row.Last, VectorBits(row.Minimum), VectorBits(row.Maximum),
                _excluded.Contains(row.Handle) ? "excluded" : "selected")).Distinct().Order(StringComparer.Ordinal).ToArray();
            string text = string.Join("\n", provenance);
            string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            physicsLease.AssertCurrent();
            return new(this, physicsLease, snapshot, exclusions, digest);
        }
    }

    internal static string Bits(float value) => BitConverter.SingleToInt32Bits(value).ToString("X8", CultureInfo.InvariantCulture);
    internal static string VectorBits(Vector3 value) => $"{Bits(value.X)}/{Bits(value.Y)}/{Bits(value.Z)}";
}
