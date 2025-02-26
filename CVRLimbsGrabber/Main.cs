﻿using System.Collections.Generic;
using System;
using UnityEngine;
using MelonLoader;
using HarmonyLib;
using RootMotion.FinalIK;
using ABI_RC.Systems.IK.SubSystems;
using ABI_RC.Systems.MovementSystem;
using ABI_RC.Core.Util.AssetFiltering;
using System.Linq;
using System.Reflection;
using ml_prm;

[assembly: MelonGame("Alpha Blend Interactive", "ChilloutVR")]
[assembly: MelonInfo(typeof(Koneko.LimbGrabber), "CVRLimbsGrabber", "1.0.0", "Exterrata")]
[assembly: MelonOptionalDependencies("PlayerRagdollMod")]
[assembly: HarmonyDontPatchAll]

namespace Koneko;
public class LimbGrabber : MelonMod
{
    public static readonly MelonPreferences_Category Category = MelonPreferences.CreateCategory("LimbGrabber");
    public static readonly MelonPreferences_Entry<bool> Enabled = Category.CreateEntry<bool>("Enabled", true);
    public static readonly MelonPreferences_Entry<bool> EnableHands = Category.CreateEntry<bool>("EnableHands", true);
    public static readonly MelonPreferences_Entry<bool> EnableFeet = Category.CreateEntry<bool>("EnableFeet", true);
    public static readonly MelonPreferences_Entry<bool> EnableHead = Category.CreateEntry<bool>("EnableHead", true);
    public static readonly MelonPreferences_Entry<bool> EnableHip = Category.CreateEntry<bool>("EnableHip", true);
    public static readonly MelonPreferences_Entry<bool> EnableRoot = Category.CreateEntry<bool>("EnableRoot", true);
    public static readonly MelonPreferences_Entry<bool> PreserveMomentum = Category.CreateEntry<bool>("PreserveMomentum", true);
    public static readonly MelonPreferences_Entry<bool> Friend = Category.CreateEntry<bool>("FriendsOnly", true);
    public static readonly MelonPreferences_Entry<bool> RagdollRelease = Category.CreateEntry<bool>("RagdollOnRelease", true);
    public static readonly MelonPreferences_Entry<bool> Debug = Category.CreateEntry<bool>("Debug", false);
    public static readonly MelonPreferences_Entry<float> VelocityMultiplier = Category.CreateEntry<float>("VelocityMultiplier", 1f);
    public static readonly MelonPreferences_Entry<float> GravityMultiplier = Category.CreateEntry<float>("GravityMultiplier", 1f);
    public static readonly MelonPreferences_Entry<float> Distance = Category.CreateEntry<float>("Distance", 0.15f);
    //  LeftHand = 0
    //  LeftFoot = 1
    //  RightHand = 2
    //  RightFoot = 3
    //  Head = 4
    //  Hip = 5
    //  Root = 6
    public static readonly string[] LimbNames = { "LeftHand", "LeftFoot", "RightHand", "RightFoot", "Head", "Hip"};
    public static MelonPreferences_Entry<bool>[] enabled;
    public static bool[] tracking;
    public static Limb[] Limbs;
    public static Transform PlayerLocal;
    public static Transform Neck;
    public static Transform RootParent;
    public static bool RootGrabbed;
    public static Vector3 RootOffset;
    public static Vector3 LastRootPosition;
    public static Vector3 Velocity;
    public static Vector3[] AverageVelocities;
    public static int VelocityIndex;
    public static IKSolverVR IKSolver;
    public static bool Initialized;
    public static bool IsAirborn;
    public static bool PrmExists;

    public struct Limb
    {
        public Transform limb;
        public Transform Parent;
        public Transform Target;
        public Transform PreviousTarget;
        public Quaternion RotationOffset;
        public Vector3 PositionOffset;
        public bool Grabbed;
    }

    public override void OnInitializeMelon()
    {
        MelonLogger.Msg("Starting");
        tracking = new bool[6];
        AverageVelocities = new Vector3[10];
        enabled = new MelonPreferences_Entry<bool>[7] {
            EnableHands,
            EnableFeet,
            EnableHands,
            EnableFeet,
            EnableHead,
            EnableHip,
            EnableRoot
        };

        HarmonyInstance.PatchAll(typeof(Patches));

        var propWhitelist = Traverse.Create(typeof(SharedFilter)).Field<HashSet<Type>>("_spawnableWhitelist").Value;
        propWhitelist.Add(typeof(GrabberComponent));

        var avatarWhitelist = Traverse.Create(typeof(SharedFilter)).Field<HashSet<Type>>("_avatarWhitelist").Value;
        avatarWhitelist.Add(typeof(GrabberComponent));

        MovementSystem.OnPlayerTeleported.AddListener(StopFall);
    }

    public override void OnSceneWasInitialized(int buildIndex, string sceneName)
    {
        if (buildIndex == 3)
        {
            Limbs = new Limb[6];
            PlayerLocal = GameObject.Find("_PLAYERLOCAL").transform;
            for (int i = 0; i < Limbs.Length; i++)
            {
                var limb = new GameObject("LimbGrabberTarget").transform;
                Limbs[i].Target = limb;
                limb.parent = PlayerLocal;
            }
            if (RegisteredMelons.Any(it => it.Info.Name == "PlayerRagdollMod"))
            {
                RagdollSupport.Initialize();
                PrmExists = true;
            }
        }
    }

    public override void OnUpdate()
    {
        if (!Initialized || !Enabled.Value) return;
        for (int i = 0; i < Limbs.Length; i++)
        {
            if (Limbs[i].Grabbed && Limbs[i].Parent != null)
            {
                Vector3 offset = Limbs[i].Parent.rotation * Limbs[i].PositionOffset;
                Limbs[i].Target.position = Limbs[i].Parent.position + offset;
                Limbs[i].Target.rotation = Limbs[i].Parent.rotation * Limbs[i].RotationOffset;
            }
        }
        if (EnableRoot.Value && Neck != null && MovementSystem.Instance.canFly)
        {
            if (PreserveMomentum.Value)
            {
                AverageVelocities[VelocityIndex] = PlayerLocal.position - LastRootPosition;
                LastRootPosition = PlayerLocal.position;
                VelocityIndex++;
                if (VelocityIndex == AverageVelocities.Length)
                {
                    VelocityIndex = 0;
                }
            }
            if (RootGrabbed) PlayerLocal.position = RootParent.position + RootOffset;
            else if (IsAirborn)
            {
                if (PreserveMomentum.Value)
                {
                    LastRootPosition = PlayerLocal.position;
                    Velocity.y -= MovementSystem.Instance.gravity * 0.01f * Time.deltaTime * GravityMultiplier.Value;
                    PlayerLocal.position += Velocity;
                    Velocity = PlayerLocal.position - LastRootPosition;
                }
                if (Physics.CheckCapsule(PlayerLocal.position, Limbs[4].Target.position, 0.2f, MovementSystem.Instance.groundMask, QueryTriggerInteraction.Ignore) || MovementSystem.Instance.flying)
                {
                    if(Debug.Value) MelonLogger.Msg("Landed");
                    if (PrmExists && RagdollRelease.Value) MelonCoroutines.Start(RagdollSupport.WaitToggleRagdoll());
                    IsAirborn = false;
                    MovementSystem.Instance.canMove = true;
                }
            }
        }
    }

    public static void Grab(GrabberComponent grabber)
    {
        if (!Enabled.Value || BodySystem.isCalibrating) return;
        if (Debug.Value) MelonLogger.Msg("grab was detected");
        int closest = 0;
        float distance = float.PositiveInfinity;
        for (int i = 0; i < 7; i++)
        {
            float dist = 0;
            if (i == 6) dist = Vector3.Distance(grabber.transform.position, Neck.position);
            else dist = Vector3.Distance(grabber.transform.position, Limbs[i].limb.position);
            if (dist < distance)
            {
                distance = dist;
                closest = i;
            }
        }
        if (distance < Distance.Value)
        {
            if (!enabled[closest].Value) return;
            if (closest == 6)
            {
                if (!MovementSystem.Instance.canFly) return;
                grabber.Limb = closest;
                if (Debug.Value) MelonLogger.Msg("limb " + Neck.name + " was grabbed by " + grabber.transform.name);
                RootOffset = PlayerLocal.position - grabber.transform.position;
                RootParent = grabber.transform;
                MovementSystem.Instance.canMove = false;
                RootGrabbed = true;
                IsAirborn = true;
                return;
            }
            grabber.Limb = closest;
            if (Debug.Value) MelonLogger.Msg("limb " + Limbs[closest].limb.name + " was grabbed by " + grabber.transform.name);
            Limbs[closest].PositionOffset = Quaternion.Inverse(grabber.transform.rotation) * (Limbs[closest].limb.position - grabber.transform.position);
            Limbs[closest].RotationOffset = Quaternion.Inverse(grabber.transform.rotation) * Limbs[closest].limb.rotation;
            Limbs[closest].Parent = grabber.transform;
            Limbs[closest].Grabbed = true;
            SetTarget(closest, Limbs[closest].Target);
            SetTracking(closest, true);
        }
    }

    public static void Release(GrabberComponent grabber)
    {
        int limb = grabber.Limb;
        if (limb == -1) return;
        grabber.Limb = -1;
        if (limb == 6)
        {
            if (grabber.transform != RootParent) return;
            if (Debug.Value) MelonLogger.Msg("limb " + Neck.name + " was released by " + grabber.transform.name);
            if (!PreserveMomentum.Value) MovementSystem.Instance.canMove = true;
            else
            {
                for (int i = 0; i < AverageVelocities.Length; i++)
                {
                    Velocity += AverageVelocities[i];
                }
                Velocity /= AverageVelocities.Length;
                Velocity *= VelocityMultiplier.Value;
            }
            RootGrabbed = false;
            if (PrmExists && RagdollRelease.Value) RagdollSupport.ToggleRagdoll();
            return;
        }
        if (grabber.transform != Limbs[limb].Parent) return;
        if (Debug.Value) MelonLogger.Msg("limb " + Limbs[limb].limb.name + " was released by " + grabber.transform.name);
        Limbs[limb].Grabbed = false;
        SetTarget(limb, Limbs[limb].PreviousTarget);
        if (!tracking[limb]) SetTracking(limb, false);
    }

    public static void SetTarget(int index, Transform Target)
    {
        switch (index)
        {
            case 0:
                IKSolver.leftArm.target = Target;
                break;
            case 1:
                IKSolver.leftLeg.target = Target;
                break;
            case 2:
                IKSolver.rightArm.target = Target;
                break;
            case 3:
                IKSolver.rightLeg.target = Target;
                break;
            case 4:
                IKSolver.spine.headTarget = Target;
                break;
            case 5:
                IKSolver.spine.pelvisTarget = Target;
                break;
        }
    }

    public static void SetTracking(int index, bool value)
    {
        switch (index)
        {
            case 0:
                BodySystem.TrackingLeftArmEnabled = value;
                break;
            case 1:
                BodySystem.TrackingLeftLegEnabled = value;
                break;
            case 2:
                BodySystem.TrackingRightArmEnabled = value;
                break;
            case 3:
                BodySystem.TrackingRightLegEnabled = value;
                break;
            case 4:
                IKSolver.spine.positionWeight = value ? 1 : 0;
                break;
            case 5:
                IKSolver.spine.pelvisPositionWeight = value ? 1 : 0;
                break;
        }
    }

    public static void StopFall()
    {
        IsAirborn = false;
        MovementSystem.Instance.canMove = true;
    }
}