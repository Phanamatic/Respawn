namespace Game.Net
{
    // Convenience enum for UI and code clarity. Mirrors _activeSlot network byte.
    public enum WeaponSlot : byte { Primary = 0, Secondary = 1, Melee = 2, Utility = 3 }
}
// Simple shared enum used across future weapon scripts.