namespace KhaozEngine.Attachments;

/// <summary>The reasons AttachmentHierarchy input is refused.</summary>
public enum AttachmentHierarchyRefusal
{
    /// <summary>Refuses a node table with another JSON shape.</summary>
    NodesNotArray,
    /// <summary>Refuses a non-object node in the hierarchy.</summary>
    NodeNotObject,
    /// <summary>Refuses a child list with another JSON shape.</summary>
    ChildrenNotArray,
    /// <summary>Refuses a child node index outside the hierarchy.</summary>
    ChildIndexOutsideNodes,
    /// <summary>Refuses multiple parents or repeated child references.</summary>
    MultipleParents,
    /// <summary>Refuses a cycle anywhere in the node hierarchy.</summary>
    Cycle,
    /// <summary>Refuses skin joints that do not share one tree root.</summary>
    JointsHaveDifferentRoots,
}
