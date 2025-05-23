using UnityEngine;
using static Player;

/// <summary>
/// All entities that can be attacked, have a HP bar etc should extend this.
/// </summary>
public interface IDamageable
{
    float HpAlpha { get; }
    Faction Faction { get; }
    Vector3 GetClosestPosition(Vector3 position);
    void ApplyDamage(int value);
}
