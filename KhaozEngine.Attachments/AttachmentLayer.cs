namespace KhaozEngine.Attachments;

/// <summary>
/// The layer a worn set sits in, from the skin outward. A higher layer hides the regions it covers on the body and on
/// every lower layer. The values are ordered, so compare layers by value.
/// </summary>
public enum AttachmentLayer : byte
{
    /// <summary>The body itself, below everything worn.</summary>
    Body = 0,

    /// <summary>Pieces worn against the skin.</summary>
    Underclothes = 1,

    /// <summary>Ordinary clothing worn over underclothes.</summary>
    Garment = 2,

    /// <summary>Protective pieces worn over garments.</summary>
    Armour = 3,

    /// <summary>Pieces worn over everything else, such as a cloak.</summary>
    Outerwear = 4,
}
