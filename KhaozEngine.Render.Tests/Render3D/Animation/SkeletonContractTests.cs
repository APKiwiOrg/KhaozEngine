using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation
{
    public class SkeletonContractTests
    {
        [Fact]
        public void RefusesADuplicateName()
        {
            var error = Assert.Throws<ArgumentException>(() => new SkeletonContract(new[]
            {
                new ContractJoint("root", null, false),
                new ContractJoint("hips", "root", true),
                new ContractJoint("spine", "hips", true),
                new ContractJoint("hips", "root", true),
            }));

            Assert.Contains("'hips'", error.Message, StringComparison.Ordinal);
            Assert.Contains("twice", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RefusesAParentNotDeclaredBefore()
        {
            var late = Assert.Throws<ArgumentException>(() => new SkeletonContract(new[]
            {
                new ContractJoint("root", null, false),
                new ContractJoint("spine", "hips", true),
                new ContractJoint("hips", "root", true),
            }));
            Assert.Contains("'spine'", late.Message, StringComparison.Ordinal);
            Assert.Contains("'hips'", late.Message, StringComparison.Ordinal);

            var own = Assert.Throws<ArgumentException>(() => new SkeletonContract(new[]
            {
                new ContractJoint("root", null, false),
                new ContractJoint("tail", "tail", true),
            }));
            Assert.Contains("'tail'", own.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RefusesASecondRootAndAnEmptyTable()
        {
            var second = Assert.Throws<ArgumentException>(() => new SkeletonContract(new[]
            {
                new ContractJoint("root", null, false),
                new ContractJoint("hips", "root", true),
                new ContractJoint("prop", null, false),
            }));
            Assert.Contains("'prop'", second.Message, StringComparison.Ordinal);
            Assert.Contains("'root'", second.Message, StringComparison.Ordinal);

            var notFirst = Assert.Throws<ArgumentException>(() => new SkeletonContract(new[]
            {
                new ContractJoint("hips", "root", true),
                new ContractJoint("root", null, false),
            }));
            Assert.Contains("'hips'", notFirst.Message, StringComparison.Ordinal);

            Assert.Throws<ArgumentException>(() => new SkeletonContract(Array.Empty<ContractJoint>()));
            Assert.Throws<ArgumentNullException>(() => new SkeletonContract(null!));
        }

        [Fact]
        public void KeepsTheJointsInOrderAndNamesTheRoot()
        {
            var source = new List<ContractJoint>
            {
                new("root", null, false),
                new("hips", "root", true),
                new("tail_01", "hips", true),
                new("socket_cut", "hips", false),
            };
            ContractJoint[] expected = source.ToArray();

            var contract = new SkeletonContract(source);
            source.Add(new ContractJoint("tail_02", "tail_01", true));

            Assert.Equal(expected, contract.Joints);
            Assert.Equal("root", contract.Root);
            foreach (ContractJoint joint in expected) Assert.True(contract.Contains(joint.Name));
            Assert.False(contract.Contains("tail_02"));
            Assert.False(contract.Contains("Hips"));
        }
    }
}
