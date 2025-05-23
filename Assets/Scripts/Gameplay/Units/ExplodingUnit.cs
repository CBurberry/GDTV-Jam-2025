using System;
using System.Collections;
using System.Linq;
using UnityEngine;

public class ExplodingUnit : AUnitInteractableUnit
{
    private const string ANIMATION_BOOL_ACTIVE = "IsActive";            //Goblin is peeking out of the barrel
    private const string ANIMATION_TRIG_FIRE = "Fire";                  //Trigger once to move to flashing with an in animation playing between
    private const string ANIMATION_TRIG_DISARM = "Disarm";              //Trigger once to move to active idle/run with an out animation playing

    private const string ANIMSTATE_IDLE_OPEN = "Idle_Out";
    private const string ANIMSTATE_PRIME_TRANSITION = "In (Fire)";
    private const string ANIMSTATE_PRIMED = "Fired";

    [SerializeField]
    private GameObject explosionPrefab;

    [SerializeField]
    private bool shouldExplodeOnDeath;

    [SerializeField]
    private float timeToExplode;

    [SerializeField]
    private float explosionRadius;

    [SerializeField]
    private float timeBeforeIdleIn;

    [SerializeField]
    private bool canDestroyMine;

    private bool hasExploded;
    private float elapsedIdleTime;

    //Other units can only attack this unit or do nothing
    public override UnitInteractContexts GetApplicableContexts(SimpleUnit unit)
        => unit.Faction != Faction ? UnitInteractContexts.Attack : UnitInteractContexts.None;

    protected override void Awake()
    {
        base.Awake();
        hasExploded = false;
        elapsedIdleTime = 0f;
    }

    protected override void Update()
    {
        base.Update();

        if (IsIdlingOpen())
        {
            elapsedIdleTime += Time.deltaTime;
            if (elapsedIdleTime >= timeBeforeIdleIn) 
            {
                elapsedIdleTime = 0f;
                animator.SetBool(ANIMATION_BOOL_ACTIVE, false);
            }
        }

        //TODO: Add a check for an enemy nearby, if so - set active bool and start attacking
    }

    protected override void ResolveResourceInteraction(IUnitInteractable target, UnitInteractContexts context)
    {
        //Only attackable if a goblin unit and the mine has pawns in it / is active
        if (Faction != Player.Faction.Goblins && target is GoldMine)
        {
            return;
        }

        GoldMine goldMine = target as GoldMine;
        if (goldMine.State == GoldMine.Status.Active)
        {
            interactionTarget = target;
            MoveTo(goldMine.transform, StartAttackMine, false, stopAtAttackDistance: true);
        }
    }

    protected override void ResolveBuildingInteraction(IUnitInteractable target, UnitInteractContexts context)
    {
        IDamageable damagableTarget = target as IDamageable;
        if (damagableTarget != null && damagableTarget.HpAlpha > 0f && damagableTarget.Faction != Faction)
        {
            interactionTarget = target;
            MoveTo((target as MonoBehaviour).transform, StartAttacking, false, stopAtAttackDistance: true);
        }
        else
        {
            throw new NotImplementedException($"[{nameof(ExplodingUnit)}.{nameof(ResolveBuildingInteraction)}]: Context resolution not implemented for {nameof(ExplodingUnit)} & {context}!");
        }
    }

    protected override void ResolveDamagableInteraction(IUnitInteractable target, UnitInteractContexts context)
    {
        if (target is Sheep || target is MeleeUnit)
        {
            MoveTo((target as MonoBehaviour).transform, StartAttacking, stopAtAttackDistance: true);
            interactionTarget = target;
        }
        else
        {
            throw new NotImplementedException($"[{nameof(MeleeUnit)}.{nameof(ResolveDamagableInteraction)}]: Context resolution not implemented for {nameof(MeleeUnit)} & {context}!");
        }
    }

    public override bool MoveTo(Vector3 worldPosition, Action onComplete = null, bool clearTarget = true, bool stopAtAttackDistance = false)
    {
        animator.SetBool(ANIMATION_BOOL_ACTIVE, true);
        return base.MoveTo(worldPosition, onComplete, clearTarget, stopAtAttackDistance);
    }

    public override void ApplyDamage(int value)
    {
        //Taking damage should activate this unit
        if (value > 0)
        {
            animator.SetBool(ANIMATION_BOOL_ACTIVE, true);
        }

        base.ApplyDamage(value);
    }

    private bool IsPrimedToExplode()
    {
        AnimatorStateInfo stateInfo = animator.GetCurrentAnimatorStateInfo(0);
        return stateInfo.IsName(ANIMSTATE_PRIME_TRANSITION) || stateInfo.IsName(ANIMSTATE_PRIMED);
    }

    private bool IsIdlingOpen() => animator.GetCurrentAnimatorStateInfo(0).IsName(ANIMSTATE_IDLE_OPEN);

    //Start a cycle of MoveTo unit until close and start a timer to explode self
    private void StartAttacking()
    {
        //TODO: Properly handle behaviour changes between active and inactive
        //For now, just set to always be active once we start attacking
        animator.SetBool(ANIMATION_BOOL_ACTIVE, true);
        StartCoroutine(Attacking());
    }

    //Move to, countdown timer, if enemy still close, go boom, otherwise MoveTo again
    private IEnumerator Attacking()
    {
        IDamageable damageTarget = interactionTarget as IDamageable;
        Func<bool> condition = () => damageTarget != null && damageTarget.HpAlpha > 0f;
        while (condition.Invoke())
        {
            if (!IsTargetWithinDistance(damageTarget, out _))
            {
                //Disarm and move
                if (IsPrimedToExplode())
                {
                    animator.SetTrigger(ANIMATION_TRIG_DISARM);
                }

                MoveTo((interactionTarget as MonoBehaviour).transform, StartAttacking, false, stopAtAttackDistance: true);
                yield break;
            }
            else
            {
                //Arm, set timer and explode after another proximity check
                animator.SetTrigger(ANIMATION_TRIG_FIRE);
                float time = Time.time;
                yield return new WaitForEndOfFrame();
                yield return RuntimeStatics.CoroutineUtilities.WaitForSecondsWithInterrupt(timeToExplode,
                    () => !condition.Invoke() && !IsTargetWithinDistance(damageTarget, out _));

                //If we broke early out of the check, the previous yield was interrupted
                if (Time.time - time > timeToExplode)
                {
                    Explode();
                    yield break;
                }
            }
        }

        //TODO: Seek another target or return to an idle routine
        //For now just disarm if we lose a target
        animator.SetBool(ANIMATION_BOOL_ACTIVE, false);
    }

    private void StartAttackMine()
    {
        animator.SetBool(ANIMATION_BOOL_ACTIVE, true);
        StartCoroutine(AttackMine());
    }

    private IEnumerator AttackMine()
    {
        GoldMine mine = interactionTarget as GoldMine;
        Vector3 closestPosition;
        Func<bool> condition = () => mine != null && mine.State == GoldMine.Status.Active;
        while (condition.Invoke())
        {
            closestPosition = mine.SpriteRenderer.bounds.ClosestPoint(transform.position);
            float magnitude = (closestPosition - transform.position).magnitude;

            //Check we are at the target (proximity check? bounds?)
            if (magnitude > data.AttackDistance)
            {
                //Disarm and move
                if (IsPrimedToExplode())
                {
                    animator.SetTrigger(ANIMATION_TRIG_DISARM);
                }

                MoveTo((interactionTarget as MonoBehaviour).transform, StartAttackMine, false, stopAtAttackDistance: true);
                yield break;
            }
            else
            {
                //Arm, set timer and explode after another check
                animator.SetTrigger(ANIMATION_TRIG_FIRE);
                float time = Time.time;
                yield return new WaitForEndOfFrame();
                yield return RuntimeStatics.CoroutineUtilities.WaitForSecondsWithInterrupt(timeToExplode, () => !condition.Invoke());

                //If we broke early out of the check, the previous yield was interrupted
                if (Time.time - time > timeToExplode)
                {
                    Explode();
                    yield break;
                }
            }
        }

        //TODO: Seek another target or return to an idle routine
        //For now just disarm if we lose a target
        animator.SetBool(ANIMATION_BOOL_ACTIVE, false);
    }

    //Disable skull prefab as we're blowing to bits when exploding
    protected override void TriggerDeath()
    {
        if (!hasExploded && shouldExplodeOnDeath)
        {
            Explode();
        }

        base.TriggerDeath();
    }

    private void Explode()
    {
        hasExploded = true;
        Instantiate(explosionPrefab, transform.position, Quaternion.identity, transform.parent);

        //Get all enemies (and structures) in a radius around this unit and apply damage to them
        var hitTargets = Physics2D.OverlapCircleAll(transform.position, explosionRadius)
            .Where(x => x != null && (x.gameObject.TryGetComponent<IDamageable>(out _) || x.gameObject.TryGetComponent<GoldMine>(out _)));

        foreach (var colliders in hitTargets)
        {
            if (colliders.gameObject.TryGetComponent(out IDamageable damageable))
            {
                damageable.ApplyDamage(data.BaseAttackDamage);
            }

            if (canDestroyMine && colliders.gameObject.TryGetComponent(out GoldMine mine))
            {
                //We'll just go ahead and be brutal here
                mine.DestroyMine();
            }
        }
    }
}