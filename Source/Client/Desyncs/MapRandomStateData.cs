using System.Collections.Generic;

namespace Multiplayer.Client
{
    /// <summary>
    /// Holds the random states for a given map, and its map id
    /// </summary>
    public class MapRandomStateData(int mapId)
    {
        public int mapId = mapId;
        public List<uint> randomStates = new();
        // PawnFloatState hash after each map tick, same indices as randomStates
        public List<int> floatStateHashes = new();
        // Map tick of each entry, local only (not sent), to report where the floats drifted
        public List<int> mapTicks = new();
    }
}
