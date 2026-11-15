namespace KhaozEngine.Locomotion;

// Engine-only fact for audited pins that write valid complete contacts or nothing into a
// zeroed owned lane, including on refusal or throw. Consumer pins cannot opt into this contract.
internal interface IMovementCompleteWaterContactWriter { }
