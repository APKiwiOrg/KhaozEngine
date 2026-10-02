using System.Threading.Tasks;
using KhaozEngine.WorldStore;

namespace KhaozEngine.NetWorld;

/// <summary>Loads or converts a legacy player record when its primary record is missing. Null keeps the join's spawn.
/// Receives the original session's captured identities and may continue off the host thread. Do not read live slot
/// state, apply game state or retire legacy data here. Returned records use the normal guarded validation, quarantine,
/// position and blob restore, and resume-hint path. An accepted record stays dirty until its first primary save.</summary>
public delegate Task<PlayerRecord?> PlayerRecordFallbackLoad(PersistenceLoadRequest request);
