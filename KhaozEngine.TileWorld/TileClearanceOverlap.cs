namespace KhaozEngine.TileWorld;

/// <summary>One penetrating subject-obstacle pair found by an authoring clearance rule.</summary>
/// <param name="RuleName">The authored rule that refused the pair.</param>
/// <param name="SubjectObjectId">The subject's document-unique object id.</param>
/// <param name="ObstacleObjectId">The obstacle's document-unique object id.</param>
/// <param name="Depth">The deepest sampled penetration into the obstacle, in world metres.</param>
public readonly record struct TileClearanceOverlap(string RuleName, long SubjectObjectId, long ObstacleObjectId,
    float Depth);
