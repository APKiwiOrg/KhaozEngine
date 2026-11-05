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
            var error = Assert.Throws<SkeletonContractArgumentException>(() => new SkeletonContract(new[]
            {
                new ContractJoint("root", null, false),
                new ContractJoint("hips", "root", true),
                new ContractJoint("spine", "hips", true),
                new ContractJoint("hips", "root", true),
            }));

            Assert.Equal(SkeletonContractRefusal.DuplicateJointName, error.Reason);
            Assert.Equal("hips", error.JointName);
            Assert.Equal("joints", error.ParamName);
            Assert.IsAssignableFrom<ArgumentException>(error);
            Assert.True(new SkeletonContract(new[]
            {
                new ContractJoint("root", null, false),
                new ContractJoint("hips", "root", true),
                new ContractJoint("spine", "hips", true),
                new ContractJoint("other", "root", true),
            }).Contains("other"));
        }

        [Fact]
        public void RefusesAParentNotDeclaredBefore()
        {
            var late = Assert.Throws<SkeletonContractArgumentException>(() => new SkeletonContract(new[]
            {
                new ContractJoint("root", null, false),
                new ContractJoint("spine", "hips", true),
                new ContractJoint("hips", "root", true),
            }));
            Assert.Equal(SkeletonContractRefusal.ParentNotDeclaredBeforeChild, late.Reason);
            Assert.Equal("spine", late.JointName);
            Assert.Equal("hips", late.ParentName);
            Assert.Equal("joints", late.ParamName);

            var own = Assert.Throws<SkeletonContractArgumentException>(() => new SkeletonContract(new[]
            {
                new ContractJoint("root", null, false),
                new ContractJoint("tail", "tail", true),
            }));
            Assert.Equal(SkeletonContractRefusal.ParentNotDeclaredBeforeChild, own.Reason);
            Assert.Equal("tail", own.JointName);
            Assert.Equal("tail", own.ParentName);
            Assert.True(new SkeletonContract(new[]
            {
                new ContractJoint("root", null, false),
                new ContractJoint("hips", "root", true),
                new ContractJoint("spine", "hips", true),
                new ContractJoint("tail", "root", true),
            }).Contains("tail"));
        }

        [Fact]
        public void RefusesASecondRootAndAnEmptyTable()
        {
            var second = Assert.Throws<SkeletonContractArgumentException>(() => new SkeletonContract(new[]
            {
                new ContractJoint("root", null, false),
                new ContractJoint("hips", "root", true),
                new ContractJoint("prop", null, false),
            }));
            Assert.Equal(SkeletonContractRefusal.AdditionalRoot, second.Reason);
            Assert.Equal("prop", second.JointName);
            Assert.Equal("root", second.RootName);
            Assert.True(new SkeletonContract(new[]
            {
                new ContractJoint("root", null, false),
                new ContractJoint("hips", "root", true),
                new ContractJoint("prop", "root", false),
            }).Contains("prop"));

            var notFirst = Assert.Throws<SkeletonContractArgumentException>(() => new SkeletonContract(new[]
            {
                new ContractJoint("hips", "root", true),
                new ContractJoint("root", null, false),
            }));
            Assert.Equal(SkeletonContractRefusal.FirstJointHasParent, notFirst.Reason);
            Assert.Equal("hips", notFirst.JointName);
            Assert.Equal("root", notFirst.ParentName);

            var empty = Assert.Throws<SkeletonContractArgumentException>(
                () => new SkeletonContract(Array.Empty<ContractJoint>()));
            Assert.Equal(SkeletonContractRefusal.EmptyJointTable, empty.Reason);
            Assert.Equal(0, empty.JointCount);
            Assert.Equal("root", new SkeletonContract(new[] { new ContractJoint("root", null, false) }).Root);
            Assert.Throws<ArgumentNullException>(() => new SkeletonContract(null!));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void RefusesAnUnnamedJoint(string? name)
        {
            var error = Assert.Throws<SkeletonContractArgumentException>(() => new SkeletonContract(new[]
            {
                new ContractJoint("root", null, false),
                new ContractJoint(name!, "root", true),
            }));
            Assert.Equal(SkeletonContractRefusal.JointUnnamed, error.Reason);
            Assert.Equal(1, error.JointIndex);
            Assert.Equal(name, error.JointName);
            Assert.Equal("joints", error.ParamName);
            Assert.True(new SkeletonContract(new[]
            {
                new ContractJoint("root", null, false),
                new ContractJoint("hips", "root", true),
            }).Contains("hips"));
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
