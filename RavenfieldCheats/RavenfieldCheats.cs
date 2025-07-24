using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using SysAction = System.Action;

[assembly: MelonInfo(typeof(RavenfieldCheats.RavenfieldCheatsMod), "Ravenfield Cheats", "1.0.0", "Arxhsz")]
[assembly: MelonGame("SteelRaven7", "Ravenfield")]

namespace RavenfieldCheats
{
    public class RavenfieldCheatsMod : MelonMod
    {
        public static RavenfieldCheatsMod Instance;

        // -------- UI / toggles --------
        public bool showMenu;
        public bool noReload, autoMaxAmmo, rainbowOverlay, instaKill, killAllEnemies, resetPlayer;
        public bool godMode;
        public bool antiRagdollSwim;
        public float swimSpeed = 30f;
        public bool verboseLogs;
        public bool walkSpeedHack, noRecoil, fullAuto, infiniteJump;
        public float walkSpeedMult = 1f;

        // State tracking
        private bool pendingWeaponCleanup;
        private string lastWeaponName = string.Empty;
        private bool prevNoReload, prevInstaKill, prevRainbow, prevAutoAmmo, prevGod, prevAntiRS;
        private bool prevVerbose, prevWalkSpeed, prevNoRecoil, prevFullAuto, prevInfJump;

        private const int MAX_AMMO = 999;

        // Harmony
        private static HarmonyLib.Harmony _harmony;

        // -------- Reflection cache --------
        private static bool reflectionInitialized;
        private static FieldInfo weaponAmmoFI;

        // Physics.SyncTransforms wrapper
        private static SysAction syncTransforms;

        // Actor / ragdoll / water fields
        private static FieldInfo inWaterFI;
        private static FieldInfo fallenOverFI;
        private static MethodInfo stopRagdollMI;
        private static MethodInfo isRagdollMI;
        private static MethodInfo endRagdollMI;

        // FPC / input / char ctrl
        private static MethodInfo enableInputMI;
        private static FieldInfo inputEnabledFI; // optional
        private static FieldInfo charCtrlFI;

        // Weapon visibility
        private static MethodInfo weaponUnholsterMI;
        private static FieldInfo weaponHolsteredFI;
        private static FieldInfo weaponSwitchLockedFI;

        // FirstPersonController speed/jump fields
        private static FieldInfo fpcWalkFI, fpcRunFI, fpcJumpFlagFI, fpcMoveDirFI, fpcJumpSpeedFI;
        private static MethodInfo fpcResetVelocityMI;
        private static float baseWalk = -1f, baseRun = -1f;
        private static object cachedFpcObj;
        private static FpsActorController cachedFpsCtrl;
        private bool speedRestored = true;

        // Ammo maintenance timer
        private float lastAmmoTick;

        // Safe pose tracking
        private Vector3 lastSafePos;
        private Quaternion lastSafeRot = Quaternion.identity;
        private float lastGroundedTime;
        private bool wasGroundedLastFrame = true;

        // Deferred pose restore
        private Vector3 queuedRestorePos;
        private Quaternion queuedRestoreRot;
        private bool needPoseRestoreNextFrame;

        // Reset coroutine
        private IEnumerator activeResetRoutine;

        // Toast system
        private struct Toast
        {
            public string text;
            public float start;
            public float dur;
            public bool active;
        }
        private Toast currentToast;
        private const float TOAST_DURATION = 3f;

        // -------- Logging helpers --------
        private void Log(string msg) { MelonLogger.Msg("[Ravenfield Cheats] " + msg); }
        internal void VLog(string msg) { if (verboseLogs) MelonLogger.Msg("[Cheats] " + msg); }
        private void LogError(string where, Exception ex)
        {
            MelonLogger.Error("[" + where + "] " + ex.GetType().Name + ": " + ex.Message);
            if (verboseLogs) MelonLogger.Error(ex.StackTrace);
        }

        // -------- Toast helpers --------
        private void ShowToast(string message)
        {
            currentToast = new Toast { text = message, start = Time.time, dur = TOAST_DURATION, active = true };
        }
        private void UpdateToast()
        {
            if (currentToast.active && Time.time - currentToast.start >= currentToast.dur)
                currentToast.active = false;
        }
        private void DrawToast()
        {
            if (!currentToast.active) return;

            int size = 28;
            GUIStyle style = new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperCenter
            };
            Color col = rainbowOverlay ? Color.HSVToRGB((Time.time * 0.5f) % 1f, 0.9f, 1f) : new Color(1f, 0.9f, 0.1f);
            style.normal.textColor = col;

            Rect r = new Rect(0, 20, Screen.width, size + 10);

            GUIStyle shadow = new GUIStyle(style) { normal = { textColor = new Color(0, 0, 0, 0.6f) } };
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), currentToast.text, shadow);
            GUI.Label(r, currentToast.text, style);
        }

        // -------- Melon lifecycle --------
        public override void OnInitializeMelon()
        {
            Instance = this;
            InitializeReflection();

            try
            {
                _harmony = new HarmonyLib.Harmony("com.kanaa.ravenfieldcheats");
                _harmony.PatchAll();
                Log("Ravenfield Cheats v2.3.3-fix3 loaded. Press F9 to open menu.");
            }
            catch (Exception ex)
            {
                LogError("Harmony PatchAll", ex);
            }
        }

        public override void OnUpdate()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.F9)) showMenu = !showMenu;

                CheckCheatStateChanges();

                if (killAllEnemies)
                {
                    DoKillAll();
                    killAllEnemies = false;
                }

                if (resetPlayer)
                {
                    resetPlayer = false;
                    DoResetPlayer();
                }

                // Transition to antiRagdollSwim ON
                if (antiRagdollSwim && !prevAntiRS)
                    HandleMidStateRecovery();
                prevAntiRS = antiRagdollSwim;

                // Process deferred pose restore
                if (needPoseRestoreNextFrame)
                {
                    needPoseRestoreNextFrame = false;
                    RestorePoseNow();
                }

                // Cache safe pose
                Actor p = ActorManager.instance != null ? ActorManager.instance.player : null;
                if (p != null && !p.dead) CacheSafePose(p);

                // Frame updates
                MaintainAmmo();
                HandleWalkAndJump();
                HandleAntiCheatsFrameUpdate();
                UpdateToast();
            }
            catch (Exception ex)
            {
                LogError("OnUpdate", ex);
            }
        }

        public override void OnGUI()
        {
            if (showMenu) DrawMenu();
            DrawToast();
        }

        // -------- GUI --------
        private Vector2 scrollPosition = Vector2.zero;
        private void DrawMenu()
        {
            GUILayout.BeginArea(new Rect(10, 10, 340, 600), "Ravenfield Cheats v2.3.3", GUI.skin.window);

            GUIStyle bold = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 12 };

            scrollPosition = GUILayout.BeginScrollView(scrollPosition, GUILayout.Width(320), GUILayout.Height(570));

            GUILayout.Label("Combat Cheats:", bold);
            noReload = GUILayout.Toggle(noReload, "No Reload (Infinite Clip)");
            autoMaxAmmo = GUILayout.Toggle(autoMaxAmmo, "Auto-Max Spare Ammo (999)");
            instaKill = GUILayout.Toggle(instaKill, "Insta-Kill Bullets");

            GUILayout.Space(10);
            GUILayout.Label("Player Cheats:", bold);
            godMode = GUILayout.Toggle(godMode, "God Mode (No Damage)");
            antiRagdollSwim = GUILayout.Toggle(antiRagdollSwim, "Anti-Ragdoll / Swim");
            infiniteJump = GUILayout.Toggle(infiniteJump, "Infinite Jump");

            GUILayout.Space(5);
            walkSpeedHack = GUILayout.Toggle(walkSpeedHack, "Walk Speed Hack");
            GUILayout.Label("Walk Speed: x" + walkSpeedMult.ToString("F1"), GUI.skin.label);
            walkSpeedMult = GUILayout.HorizontalSlider(walkSpeedMult, 0.1f, 10f);

            GUILayout.Space(5);
            GUILayout.Label("Swim Speed: " + swimSpeed.ToString("F1"), GUI.skin.label);
            swimSpeed = GUILayout.HorizontalSlider(swimSpeed, 0f, 200f);

            noRecoil = GUILayout.Toggle(noRecoil, "No Recoil / Spread");
            fullAuto = GUILayout.Toggle(fullAuto, "Force Full-Auto (Hold to fire)");

            GUILayout.Space(10);
            GUILayout.Label("Visual Effects:", bold);
            rainbowOverlay = GUILayout.Toggle(rainbowOverlay, "Rainbow Banner Text");

            GUILayout.Space(10);
            GUILayout.Label("Settings:", bold);
            verboseLogs = GUILayout.Toggle(verboseLogs, "Verbose Logs");

            GUILayout.Space(10);
            GUILayout.Label("Actions:", bold);
            if (GUILayout.Button("Kill All Enemies")) killAllEnemies = true;
            if (GUILayout.Button("Reset Player")) resetPlayer = true;

            GUILayout.Space(10);
            GUILayout.Label("Toggle cheats to see toast notifications!", GUI.skin.label);

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // ==========================================================
        //                     CHEAT HELPERS
        // ==========================================================

        private void DoKillAll()
        {
            try
            {
                if (ActorManager.instance == null || ActorManager.instance.player == null) return;

                var enemies = ActorManager
                    .AliveActorsOnTeam(1 - ActorManager.instance.player.team)
                    .ToArray();

                foreach (Actor a in enemies)
                {
                    if (a != null && !a.dead)
                        a.Damage(9999f, 9999f, false, a.Position(), Vector3.zero, Vector3.zero);
                }
            }
            catch (Exception ex)
            {
                LogError("DoKillAll", ex);
            }
        }

        private void MaintainAmmo()
        {
            if (!autoMaxAmmo) return;
            if (Time.time - lastAmmoTick < 1f) return;

            Actor p = ActorManager.instance != null ? ActorManager.instance.player : null;
            if (p == null || p.spareAmmo == null) return;

            for (int i = 0; i < p.spareAmmo.Length; i++)
                p.spareAmmo[i] = MAX_AMMO;

            lastAmmoTick = Time.time;
        }

        private void DoResetPlayer()
        {
            if (activeResetRoutine != null)
                MelonCoroutines.Stop(activeResetRoutine);

            activeResetRoutine = ResetRoutine();
            MelonCoroutines.Start(activeResetRoutine);
        }

        private IEnumerator ResetRoutine()
        {
            Actor p = ActorManager.instance != null ? ActorManager.instance.player : null;
            if (p == null) yield break;

            Vector3 pos;
            Quaternion rot;
            if (!GetFallbackSpawnPos(out pos, out rot, p))
            {
                pos = p.Position();
                rot = p.transform.rotation;
            }

            VLog("Reset start. Target pos: " + pos);

            if (p.activeWeapon != null)
            {
                lastWeaponName = p.activeWeapon.name;
                p.activeWeapon.Hide();
                p.activeWeapon.gameObject.SetActive(false);
            }
            pendingWeaponCleanup = true;

            bool softOk = SoftResetPlayer(p, pos, rot);
            if (!softOk)
            {
                VLog("Soft reset failed, hard reset...");
                Actor original = p;
                original.Damage(9999f, 0f, false, original.Position(), Vector3.forward, Vector3.zero);

                float start = Time.time;
                const float timeout = 3f;
                while (Time.time - start < timeout)
                {
                    Actor np = ActorManager.instance != null ? ActorManager.instance.player : null;
                    if (np != null && np != original && !np.dead)
                    {
                        p = np;
                        break;
                    }
                    yield return null;
                }

                if (p == null) p = ActorManager.instance != null ? ActorManager.instance.player : original;
                if (p == null) yield break;

                if (p.dead) ForceRespawn(p, pos, rot);
            }

            try
            {
                StopAllMotion(p);
                MelonCoroutines.Start(FreezeRBsOneFrame(p));

                if (fallenOverFI != null) fallenOverFI.SetValue(p, false);

                FpsActorController fpsCtrl = p.controller as FpsActorController;
                if (fpsCtrl != null)
                {
                    SafeCall(endRagdollMI, fpsCtrl, null);
                    EnablePlayerInput(p, true);
                }

                ForceTeleport(p, pos, rot);
                VLog("Teleported to " + pos);

                CleanupPlayerWeapons(p);

                if (Time.timeScale < 1f)
                {
                    Time.timeScale = 1f;
                    Time.fixedDeltaTime = Time.timeScale / 60f;
                }

                ShowToast("PLAYER RESET");
                VLog("Reset finished");
            }
            catch (Exception ex)
            {
                LogError("ResetRoutine cleanup", ex);
                try
                {
                    FpsActorController ctrl = p.controller as FpsActorController;
                    EnablePlayerInput(p, true);
                    SafeCall(endRagdollMI, ctrl, null);
                }
                catch { }
            }
            finally
            {
                activeResetRoutine = null;
            }
        }

        private bool SoftResetPlayer(Actor player, Vector3 position, Quaternion rotation)
        {
            try
            {
                if (player == null || player.dead) return false;

                StopAllMotion(player);

                if (player.ragdoll != null && stopRagdollMI != null)
                    SafeCall(stopRagdollMI, player.ragdoll, null);

                if (fallenOverFI != null) fallenOverFI.SetValue(player, false);

                player.health = 100f;

                ForceTeleport(player, position, rotation);

                FpsActorController fpsCtrl = player.controller as FpsActorController;
                if (fpsCtrl != null)
                {
                    SafeCall(endRagdollMI, fpsCtrl, null);
                    EnablePlayerInput(player, true);
                }
                return true;
            }
            catch (Exception ex)
            {
                LogError("SoftResetPlayer", ex);
                return false;
            }
        }

        private void ForceRespawn(Actor player, Vector3 position, Quaternion rotation)
        {
            try
            {
                if (player == null) return;

                ForceTeleport(player, position, rotation);
                StopAllMotion(player);

                if (player.ragdoll != null && stopRagdollMI != null)
                    SafeCall(stopRagdollMI, player.ragdoll, null);

                if (fallenOverFI != null) fallenOverFI.SetValue(player, false);

                player.health = 100f;

                FpsActorController fpsCtrl = player.controller as FpsActorController;
                if (fpsCtrl != null)
                {
                    SafeCall(endRagdollMI, fpsCtrl, null);
                    EnablePlayerInput(player, true);
                }

                CleanupPlayerWeapons(player);
                VLog("Force respawn complete");
            }
            catch (Exception ex)
            {
                LogError("ForceRespawn", ex);
            }
        }

        private void CleanupPlayerWeapons(Actor player)
        {
            try
            {
                var weapons = player.controller != null ? player.controller.GetComponentsInChildren<Weapon>(true) : null;
                if (weapons == null || weapons.Length <= 1)
                {
                    VLog("No duplicate weapons");
                    return;
                }

                bool kept = false;
                foreach (Weapon w in weapons)
                {
                    if (w == null) continue;
                    if (!kept)
                    {
                        kept = true;
                        w.Show();
                        w.gameObject.SetActive(true);
                        VLog("Kept weapon: " + w.name);
                        continue;
                    }
                    VLog("Destroying duplicate: " + w.name);
                    UnityEngine.Object.Destroy(w.gameObject);
                }

                if (player.activeWeapon == null)
                {
                    player.activeWeapon = weapons.FirstOrDefault(x => x != null);
                    if (player.activeWeapon != null)
                    {
                        player.activeWeapon.Show();
                        player.activeWeapon.gameObject.SetActive(true);
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("CleanupPlayerWeapons", ex);
            }
        }

        // -------- Movement / Jump --------
        private void HandleWalkAndJump()
        {
            Actor p = ActorManager.instance != null ? ActorManager.instance.player : null;
            if (p == null) return;
            FpsActorController fps = p.controller as FpsActorController;
            if (fps == null) return;

            object fpc = GetFpcObject(fps);
            if (fpc == null) return;

            if (walkSpeedHack)
            {
                try
                {
                    if (baseWalk < 0f && fpcWalkFI != null && fpcRunFI != null)
                    {
                        baseWalk = (float)fpcWalkFI.GetValue(fpc);
                        baseRun = (float)fpcRunFI.GetValue(fpc);
                    }
                    if (fpcWalkFI != null && fpcRunFI != null)
                    {
                        fpcWalkFI.SetValue(fpc, baseWalk * walkSpeedMult);
                        fpcRunFI.SetValue(fpc, baseRun * walkSpeedMult);
                        speedRestored = false;
                    }
                }
                catch { }
            }
            else if (!speedRestored && baseWalk > 0f)
            {
                try
                {
                    if (fpcWalkFI != null && fpcRunFI != null)
                    {
                        fpcWalkFI.SetValue(fpc, baseWalk);
                        fpcRunFI.SetValue(fpc, baseRun);
                        speedRestored = true;
                        if (prevWalkSpeed) ShowToast("SPEED HACK OFF");
                    }
                }
                catch { }
            }

            if (infiniteJump && Input.GetKeyDown(KeyCode.Space))
            {
                try
                {
                    if (fpcJumpFlagFI != null)
                        fpcJumpFlagFI.SetValue(fpc, true);

                    if (fpcMoveDirFI != null && fpcJumpSpeedFI != null)
                    {
                        Vector3 md = (Vector3)fpcMoveDirFI.GetValue(fpc);
                        float js = (float)fpcJumpSpeedFI.GetValue(fpc);
                        md.y = js;
                        fpcMoveDirFI.SetValue(fpc, md);
                    }
                }
                catch { }
            }

            if (infiniteJump)
            {
                try
                {
                    CharacterController cc = charCtrlFI != null ? (CharacterController)charCtrlFI.GetValue(fps) : null;
                    if (cc != null && !cc.isGrounded && Input.GetKeyDown(KeyCode.Space))
                    {
                        Rigidbody rb = p.GetComponent<Rigidbody>();
                        if (rb != null) rb.velocity = new Vector3(rb.velocity.x, 6.5f, rb.velocity.z);
                    }
                }
                catch { }
            }
        }

        // -------- Toggle logger --------
        private void CheckCheatStateChanges()
        {
            LogToggleToast("NoReload", noReload, ref prevNoReload);
            LogToggleToast("InstaKill", instaKill, ref prevInstaKill);
            LogToggleToast("Rainbow Text", rainbowOverlay, ref prevRainbow);
            LogToggleToast("AutoMaxAmmo", autoMaxAmmo, ref prevAutoAmmo);
            LogToggleToast("GodMode", godMode, ref prevGod);
            LogToggleToast("AntiRagdoll/Swim", antiRagdollSwim, ref prevAntiRS);
            LogToggleToast("Verbose Logs", verboseLogs, ref prevVerbose);
            LogToggleToast("WalkSpeedHack", walkSpeedHack, ref prevWalkSpeed);
            LogToggleToast("NoRecoil", noRecoil, ref prevNoRecoil);
            LogToggleToast("FullAuto", fullAuto, ref prevFullAuto);
            LogToggleToast("InfiniteJump", infiniteJump, ref prevInfJump);
        }

        private void LogToggleToast(string label, bool now, ref bool prev)
        {
            if (now == prev) return;
            VLog(label + " -> " + (now ? "ON" : "OFF"));
            ShowToast(label + " " + (now ? "ON" : "OFF"));
            prev = now;
        }

        // -------- Safe pose / anti ragdoll --------
        private void CacheSafePose(Actor p)
        {
            FpsActorController ctrl = p.controller as FpsActorController;
            if (ctrl == null) return;

            CharacterController cc = charCtrlFI != null ? (CharacterController)charCtrlFI.GetValue(ctrl) : null;
            bool grounded = (cc != null && cc.isGrounded);

            if (grounded || (wasGroundedLastFrame && Time.time - lastGroundedTime < 0.5f))
            {
                lastSafePos = p.transform.position;
                lastSafeRot = p.transform.rotation;
                if (grounded) lastGroundedTime = Time.time;
            }
            else if (wasGroundedLastFrame && !grounded)
            {
                lastSafePos = p.transform.position;
                lastSafeRot = p.transform.rotation;
                lastGroundedTime = Time.time;
            }
            wasGroundedLastFrame = grounded;
        }

        private bool IsPositionInvalid(Vector3 position)
        {
            try
            {
                if (position == Vector3.zero) return true;
                if (position.magnitude > 100000f) return true;
                if (position.y < -90f) return true;

                if (Physics.CheckSphere(position, 0.5f, ~0, QueryTriggerInteraction.Ignore))
                    return true;

                RaycastHit hit;
                if (!Physics.Raycast(position + Vector3.up * 1000f, Vector3.down, out hit, 2000f, ~0, QueryTriggerInteraction.Ignore))
                    return true;

                return false;
            }
            catch
            {
                return true;
            }
        }

        private bool GetFallbackSpawnPos(out Vector3 pos, out Quaternion rot, Actor player)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;

            try
            {
                if (lastSafePos != Vector3.zero && !IsPositionInvalid(lastSafePos))
                {
                    pos = lastSafePos;
                    rot = lastSafeRot;
                    return true;
                }

                List<GameObject> spawnPoints = GameObject.FindObjectsOfType<GameObject>()
                    .Where(go => go != null && go.name.IndexOf("spawn", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                Vector3 ppos = player != null && player.transform != null ? player.transform.position : Vector3.zero;

                GameObject valid = spawnPoints
                    .Where(go => go != null && go.transform != null && !IsPositionInvalid(go.transform.position))
                    .OrderBy(go => Vector3.Distance(go.transform.position, ppos))
                    .FirstOrDefault();

                if (valid != null)
                {
                    pos = valid.transform.position + Vector3.up * 2f;
                    rot = valid.transform.rotation;
                    return true;
                }

                if (player != null && player.transform != null && !IsPositionInvalid(player.transform.position))
                {
                    pos = player.transform.position;
                    rot = player.transform.rotation;
                    return true;
                }

                pos = new Vector3(0, 5f, 0);
                rot = Quaternion.identity;
                return true;
            }
            catch (Exception ex)
            {
                LogError("GetFallbackSpawnPos", ex);
                pos = new Vector3(0, 5f, 0);
                rot = Quaternion.identity;
                return false;
            }
        }

        private void QueueImmediateUnragdoll(Actor player)
        {
            if (player == null || player.dead) return;

            try
            {
                Vector3 rp;
                Quaternion rr;
                if (!GetFallbackSpawnPos(out rp, out rr, player))
                {
                    rp = player.transform.position;
                    rr = player.transform.rotation;
                    if (IsPositionInvalid(rp))
                    {
                        rp += Vector3.up * 2f;
                        rr = Quaternion.identity;
                    }
                }

                queuedRestorePos = rp;
                queuedRestoreRot = rr;

                if (player.ragdoll != null && stopRagdollMI != null)
                    SafeCall(stopRagdollMI, player.ragdoll, null);

                if (fallenOverFI != null)
                    fallenOverFI.SetValue(player, false);

                FpsActorController ctrl = player.controller as FpsActorController;
                if (ctrl != null)
                {
                    SafeCall(endRagdollMI, ctrl, null);
                    EnablePlayerInput(player, true);
                    EnsureWeaponVisible(player);
                }

                needPoseRestoreNextFrame = true;
                VLog("Queued pose restore to " + rp);
                ShowToast("RECOVERED FROM RAGDOLL");
            }
            catch (Exception ex)
            {
                LogError("QueueImmediateUnragdoll", ex);
                try
                {
                    EnablePlayerInput(player, true);
                    EnsureWeaponVisible(player);
                }
                catch (Exception innerEx)
                {
                    LogError("QueueImmediateUnragdoll fallback", innerEx);
                }
            }
        }

        private void RestorePoseNow()
        {
            try
            {
                Actor player = ActorManager.instance != null ? ActorManager.instance.player : null;
                if (player == null || player.dead) return;

                Vector3 targetPos;
                Quaternion targetRot;

                if (queuedRestorePos != Vector3.zero && !IsPositionInvalid(queuedRestorePos))
                {
                    targetPos = queuedRestorePos;
                    targetRot = queuedRestoreRot != Quaternion.identity ? queuedRestoreRot : player.transform.rotation;
                }
                else
                {
                    Vector3 fp;
                    Quaternion fr;
                    if (!GetFallbackSpawnPos(out fp, out fr, player))
                    {
                        fp = player.transform.position + Vector3.up * 2f;
                        fr = player.transform.rotation;
                    }
                    targetPos = fp;
                    targetRot = fr;
                }

                queuedRestorePos = Vector3.zero;
                queuedRestoreRot = Quaternion.identity;

                if (player.ragdoll != null && stopRagdollMI != null)
                    SafeCall(stopRagdollMI, player.ragdoll, null);

                ForceTeleport(player, targetPos, targetRot);

                ResetPlayerVelocity(player);
                EnablePlayerInput(player, true);
                EnsureWeaponVisible(player);

                MelonCoroutines.Start(DelayedRecovery(player));

                VLog("Pose restored to " + targetPos);
                ShowToast("POSITION RESTORED");
            }
            catch (Exception ex)
            {
                LogError("RestorePoseNow", ex);
                try
                {
                    Actor player = ActorManager.instance != null ? ActorManager.instance.player : null;
                    if (player != null)
                    {
                        EnablePlayerInput(player, true);
                        EnsureWeaponVisible(player);
                        StopRagdoll(player);
                        needPoseRestoreNextFrame = true;
                    }
                }
                catch (Exception inner) { LogError("RestorePoseNow fallback", inner); }
            }
        }

        private IEnumerator DelayedRecovery(Actor player)
        {
            if (player == null) yield break;
            yield return null;

            try
            {
                FpsActorController ctrl = player.controller as FpsActorController;
                if (ctrl != null)
                {
                    EnablePlayerInput(player, true);

                    CharacterController cc = charCtrlFI != null ? (CharacterController)charCtrlFI.GetValue(ctrl) : null;
                    if (cc != null)
                    {
                        cc.enabled = true;
                        cc.Move(Vector3.zero);
                    }
                }

                if (player.ragdoll != null && stopRagdollMI != null)
                    SafeCall(stopRagdollMI, player.ragdoll, null);

                if (fallenOverFI != null) fallenOverFI.SetValue(player, false);

                player.transform.position += Vector3.up * 0.01f;
                player.transform.position -= Vector3.up * 0.01f;

                VLog("Delayed recovery done");
            }
            catch (Exception ex)
            {
                LogError("DelayedRecovery", ex);
            }
        }

        private void HandleMidStateRecovery()
        {
            try
            {
                Actor player = ActorManager.instance != null ? ActorManager.instance.player : null;
                if (player == null) return;

                VLog("Mid-state recovery...");

                if (player.ragdoll != null && isRagdollMI != null)
                {
                    bool rag = false;
                    try { rag = (bool)isRagdollMI.Invoke(player.ragdoll, null); } catch { }
                    if (rag)
                    {
                        QueueImmediateUnragdoll(player);
                        return;
                    }
                }

                if (inWaterFI != null)
                {
                    try
                    {
                        bool inWater = (bool)inWaterFI.GetValue(player);
                        if (inWater)
                        {
                            inWaterFI.SetValue(player, false);
                            VLog("Forced out of water");

                            if (lastSafePos != Vector3.zero)
                            {
                                Vector3 safePos = lastSafePos + Vector3.up * 1.5f;
                                ForceTeleport(player, safePos, lastSafeRot);
                                VLog("Teleported above water");
                            }
                        }
                    }
                    catch (Exception ex) { LogError("Water state", ex); }
                }

                FpsActorController controller = player.controller as FpsActorController;
                if (controller != null)
                {
                    EnablePlayerInput(player, true);
                }

                VLog("Mid-state recovery complete");
            }
            catch (Exception ex)
            {
                LogError("HandleMidStateRecovery", ex);
            }
        }

        private void HandleAntiCheatsFrameUpdate()
        {
            Actor player = ActorManager.instance != null ? ActorManager.instance.player : null;
            if (player == null) return;

            if (pendingWeaponCleanup && !player.dead)
            {
                CleanupPlayerWeapons(player);
                pendingWeaponCleanup = false;
            }

            if (!antiRagdollSwim) return;

            try
            {
                if (player.ragdoll != null && isRagdollMI != null)
                {
                    bool rag = false;
                    try { rag = (bool)isRagdollMI.Invoke(player.ragdoll, null); } catch { }
                    if (rag)
                    {
                        QueueImmediateUnragdoll(player);
                        VLog("Anti-ragdoll queued.");
                    }
                }

                if (inWaterFI != null)
                {
                    try
                    {
                        bool inWater = (bool)inWaterFI.GetValue(player);
                        if (inWater) inWaterFI.SetValue(player, false);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                LogError("HandleAntiCheatsFrameUpdate", ex);
            }
        }

        // -------- Low-level helpers --------
        private void StopAllMotion(Actor a)
        {
            if (a == null) return;

            Rigidbody rb = a.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            try
            {
                if (a.ragdoll != null)
                {
                    a.hipRigidbody.velocity = Vector3.zero;
                    a.hipRigidbody.angularVelocity = Vector3.zero;
                    a.headRigidbody.velocity = Vector3.zero;
                    a.headRigidbody.angularVelocity = Vector3.zero;

                    Rigidbody[] bodies = a.ragdoll.GetComponentsInChildren<Rigidbody>();
                    foreach (Rigidbody r in bodies)
                    {
                        r.velocity = Vector3.zero;
                        r.angularVelocity = Vector3.zero;
                        r.Sleep();
                    }
                }
            }
            catch { }

            FpsActorController fps = a.controller as FpsActorController;
            if (fps != null)
            {
                object fpc = GetFpcObject(fps);
                if (fpc != null)
                {
                    SafeCall(fpcResetVelocityMI, fpc, null);
                    if (fpcMoveDirFI != null)
                    {
                        Vector3 md = (Vector3)fpcMoveDirFI.GetValue(fpc);
                        md.y = 0f;
                        fpcMoveDirFI.SetValue(fpc, md);
                    }
                }
            }

            CharacterController cc = charCtrlFI != null && fps != null ? (CharacterController)charCtrlFI.GetValue(fps) : null;
            if (cc != null) cc.Move(Vector3.zero);

            if (syncTransforms != null) syncTransforms();
        }

        private IEnumerator FreezeRBsOneFrame(Actor p)
        {
            if (p == null) yield break;
            Rigidbody[] rbs = p.GetComponentsInChildren<Rigidbody>();
            if (rbs == null) yield break;

            foreach (Rigidbody rb in rbs)
            {
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.Sleep();
                }
            }
            yield return null;
        }

        private void ForceTeleport(Actor a, Vector3 pos, Quaternion rot)
        {
            if (a == null) return;
            try
            {
                FpsActorController ctrl = a.controller as FpsActorController;
                CharacterController cc = charCtrlFI != null && ctrl != null ? (CharacterController)charCtrlFI.GetValue(ctrl) : null;

                if (cc != null) cc.enabled = false;

                a.transform.position = pos;
                a.transform.rotation = rot;

                if (syncTransforms != null) syncTransforms();

                if (cc != null) cc.enabled = true;
            }
            catch (Exception ex)
            {
                LogError("ForceTeleport", ex);
            }
        }

        private void ResetPlayerVelocity(Actor player)
        {
            try
            {
                Rigidbody rb = player != null ? player.GetComponent<Rigidbody>() : null;
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
            catch (Exception ex)
            {
                LogError("ResetPlayerVelocity", ex);
            }
        }

        private bool IsRagdolled(Actor player)
        {
            try
            {
                if (player != null && player.ragdoll != null && isRagdollMI != null)
                    return (bool)isRagdollMI.Invoke(player.ragdoll, null);
            }
            catch { }
            return false;
        }

        private void StopRagdoll(Actor player)
        {
            try
            {
                if (player != null && player.ragdoll != null && stopRagdollMI != null)
                    SafeCall(stopRagdollMI, player.ragdoll, null);
                else
                {
                    FpsActorController ctrl = player != null ? player.controller as FpsActorController : null;
                    SafeCall(endRagdollMI, ctrl, null);
                }
            }
            catch (Exception ex)
            {
                LogError("StopRagdoll", ex);
            }
        }

        private void EnablePlayerInput(Actor player, bool enable)
        {
            if (player == null) return;
            FpsActorController ctrl = player.controller as FpsActorController;
            if (ctrl == null) return;

            try
            {
                if (inputEnabledFI != null)
                {
                    inputEnabledFI.SetValue(ctrl, enable);
                }

                if (enableInputMI != null)
                {
                    ParameterInfo[] ps = enableInputMI.GetParameters();
                    if (ps.Length == 0)
                    {
                        enableInputMI.Invoke(ctrl, null);
                    }
                    else if (ps.Length == 1 && ps[0].ParameterType == typeof(bool))
                    {
                        object[] prm = new object[1] { enable };
                        enableInputMI.Invoke(ctrl, prm);
                    }
                    else
                    {
                        // Just try invoke with null; if it fails, ignore
                        try { enableInputMI.Invoke(ctrl, null); } catch { }
                    }
                }

                if (enable) EnsureWeaponVisible(player);
            }
            catch (Exception ex)
            {
                LogError("EnablePlayerInput", ex);
            }
        }

        private void EnsureWeaponVisible(Actor player)
        {
            if (player == null) return;

            try
            {
                Weapon w = player.activeWeapon;
                if (w == null)
                {
                    Weapon[] ws = player.controller != null ? player.controller.GetComponentsInChildren<Weapon>(true) : null;
                    if (ws != null && ws.Length > 0)
                    {
                        w = ws[0];
                        player.activeWeapon = w;
                    }
                }

                if (w == null) return;

                w.gameObject.SetActive(true);
                w.Show();

                if (weaponHolsteredFI != null)
                {
                    object hol = weaponHolsteredFI.GetValue(w);
                    bool ih = hol is bool && (bool)hol;
                    if (ih && weaponUnholsterMI != null)
                        weaponUnholsterMI.Invoke(w, null);
                }

                if (weaponSwitchLockedFI != null && player.controller is FpsActorController fps)
                {
                    weaponSwitchLockedFI.SetValue(fps, false);
                }

                VLog("Ensured weapon " + w.name + " visible");
            }
            catch (Exception ex)
            {
                LogError("EnsureWeaponVisible", ex);
            }
        }

        // ==========================================================
        //                     REFLECTION
        // ==========================================================
        private void InitializeReflection()
        {
            if (reflectionInitialized) return;

            try
            {
                // Weapon ammo field
                string[] ammoNames = { "ammo", "_ammo", "currentAmmo", "clipAmmo" };
                weaponAmmoFI = TryFieldSilent(typeof(Weapon), ammoNames);
                if (weaponAmmoFI != null) MelonLogger.Msg("Found weapon ammo field: " + weaponAmmoFI.Name);
                else MelonLogger.Warning("Could not find weapon ammo field. NoReload may fail.");

                // Actor fields
                inWaterFI = TryFieldSilent(typeof(Actor), "inWater");
                if (inWaterFI != null) MelonLogger.Msg("Found Actor.inWater field.");

                fallenOverFI = TryFieldSilent(typeof(Actor), "fallenOver");
                if (fallenOverFI != null) MelonLogger.Msg("Found Actor.fallenOver field.");

                // Ragdoll methods
                stopRagdollMI = TryMethodSilentByTypeName("ActiveRaggy", "StopRagdoll");
                isRagdollMI = TryMethodSilentByTypeName("ActiveRaggy", "IsRagdoll");
                if (isRagdollMI == null || stopRagdollMI == null)
                {
                    // Try inside Ragdoll or Actor
                    Type actorType = typeof(Actor);
                    MethodInfo[] ams = actorType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    foreach (MethodInfo m in ams)
                    {
                        if (stopRagdollMI == null && m.Name.IndexOf("StopRagdoll", StringComparison.OrdinalIgnoreCase) >= 0) stopRagdollMI = m;
                        if (isRagdollMI == null && m.Name.IndexOf("IsRagdoll", StringComparison.OrdinalIgnoreCase) >= 0) isRagdollMI = m;
                    }
                }

                // FpsActorController methods
                Type fpsType = typeof(FpsActorController);
                endRagdollMI = TryMethodSilent(fpsType, "EndRagdoll");
                enableInputMI = TryMethodSilent(fpsType, "EnableInput", "SetInputEnabled");

                // CharacterController field
                charCtrlFI = TryFieldSilent(fpsType, "characterController", "_characterController");

                // Weapon visibility
                weaponHolsteredFI = TryFieldSilent(typeof(Weapon), "holstered", "_holstered", "isHolstered");
                weaponUnholsterMI = TryMethodSilent(typeof(Weapon), "Unholster", "UnHolster");
                weaponSwitchLockedFI = TryFieldSilent(fpsType, "weaponSwitchLocked", "_weaponSwitchLocked", "switchLocked");

                // FirstPersonController fields
                FindFpcFields();

                // Physics.SyncTransforms
                MethodInfo st = typeof(Physics).GetMethod("SyncTransforms", BindingFlags.Public | BindingFlags.Static);
                if (st != null) syncTransforms = (SysAction)Delegate.CreateDelegate(typeof(SysAction), st);

                reflectionInitialized = true;
                VLog("Reflection init complete");
            }
            catch (Exception ex)
            {
                LogError("InitializeReflection", ex);
            }
        }

        private void FindFpcFields()
        {
            // Try known type names
            string[] tnames = {
                "UnityStandardAssets.Characters.FirstPerson.FirstPersonController",
                "FirstPersonController",
                "UnityStandardAssets.FirstPersonController"
            };

            Type fpcType = null;
            for (int i = 0; i < tnames.Length; i++)
            {
                fpcType = AccessTools.TypeByName(tnames[i]);
                if (fpcType != null) break;
            }

            if (fpcType != null)
            {
                fpcWalkFI = TryFieldSilent(fpcType, "m_WalkSpeed");
                fpcRunFI = TryFieldSilent(fpcType, "m_RunSpeed");
                fpcJumpFlagFI = TryFieldSilent(fpcType, "m_Jump");
                fpcMoveDirFI = TryFieldSilent(fpcType, "m_MoveDir");
                fpcJumpSpeedFI = TryFieldSilent(fpcType, "m_JumpSpeed");
                fpcResetVelocityMI = TryMethodSilent(fpcType, "ResetVelocity");

                if (fpcWalkFI != null && fpcRunFI != null)
                    MelonLogger.Msg("[Ravenfield Cheats] FPC fields in " + fpcType.Name);
                else
                    MelonLogger.Warning("[Ravenfield Cheats] FPC speed fields not found in " + fpcType.Name);
            }
            else
            {
                MelonLogger.Warning("[Ravenfield Cheats] FirstPersonController type not found. Walk speed hack may fail.");
            }
        }

        private object GetFpcObject(FpsActorController fps)
        {
            if (fps == null) return null;

            if (cachedFpsCtrl == fps && cachedFpcObj != null) return cachedFpcObj;

            Component[] comps = fps.gameObject.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                Component c = comps[i];
                if (c == null) continue;
                string n = c.GetType().Name;
                if (n.IndexOf("FirstPersonController", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    cachedFpsCtrl = fps;
                    cachedFpcObj = c;
                    return cachedFpcObj;
                }
            }
            return null;
        }

        // Safe find helpers (no Harmony spam)
        private static FieldInfo TryFieldSilent(Type t, params string[] names)
        {
            BindingFlags bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            for (int i = 0; i < names.Length; i++)
            {
                FieldInfo fi = t.GetField(names[i], bf);
                if (fi != null) return fi;
            }
            return null;
        }

        private static MethodInfo TryMethodSilent(Type t, params string[] names)
        {
            BindingFlags bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            for (int i = 0; i < names.Length; i++)
            {
                MethodInfo mi = t.GetMethod(names[i], bf);
                if (mi != null) return mi;
            }
            return null;
        }

        private static MethodInfo TryMethodSilentByTypeName(string typeName, params string[] methodNames)
        {
            Type t = AccessTools.TypeByName(typeName);
            if (t == null) return null;
            return TryMethodSilent(t, methodNames);
        }

        private static void SafeCall(MethodInfo mi, object inst, object[] args)
        {
            if (mi == null || inst == null) return;
            try { mi.Invoke(inst, args); }
            catch { }
        }

        // ==========================================================
        //                     HARMONY PATCHES
        // ==========================================================

        [HarmonyPatch]
        private class Patch_NoReload
        {
            static MethodBase TargetMethod()
            {
                try { return AccessTools.Method(typeof(Weapon), "Fire"); }
                catch { return null; }
            }

            static void Postfix(Weapon __instance)
            {
                try
                {
                    if (Instance == null || !Instance.noReload || __instance == null || !reflectionInitialized) return;

                    if (weaponAmmoFI != null)
                    {
                        int maxAmmo = __instance.configuration != null ? __instance.configuration.ammo : 30;
                        weaponAmmoFI.SetValue(__instance, maxAmmo);
                    }
                    else
                    {
                        try { __instance.ammo = __instance.configuration.ammo; } catch { }
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Error("NoReload patch err: " + ex.Message);
                }
            }
        }

        [HarmonyPatch]
        private class Patch_InstaKill
        {
            static bool Prepare()
            {
                try
                {
                    return AccessTools.Method(typeof(Projectile), "Start") != null;
                }
                catch { return false; }
            }

            static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(Projectile), "Start");
            }

            static void Postfix(Projectile __instance)
            {
                try
                {
                    if (Instance == null || !Instance.instaKill || __instance == null || __instance.configuration == null) return;
                    Actor player = ActorManager.instance != null ? ActorManager.instance.player : null;
                    if (__instance.source != null && player != null && __instance.source == player)
                    {
                        __instance.configuration.damage = 9999f;
                        __instance.configuration.balanceDamage = 9999f;
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Error("InstaKill patch err: " + ex.Message);
                }
            }
        }

        [HarmonyPatch]
        private class Patch_GodMode
        {
            static bool Prepare()
            {
                try { return AccessTools.Method(typeof(Actor), "Damage") != null; }
                catch { return false; }
            }

            static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(Actor), "Damage");
            }

            static bool Prefix(Actor __instance)
            {
                try
                {
                    if (Instance != null && Instance.godMode && __instance != null &&
                        ActorManager.instance != null && ActorManager.instance.player == __instance)
                    {
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Error("GodMode patch err: " + ex.Message);
                }
                return true;
            }
        }

        [HarmonyPatch]
        private class Patch_FallOver
        {
            static bool Prepare()
            {
                try { return AccessTools.Method(typeof(Actor), "FallOver") != null; }
                catch { return false; }
            }

            static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(Actor), "FallOver");
            }

            static bool Prefix(Actor __instance)
            {
                try
                {
                    if (__instance != null && ActorManager.instance != null &&
                        ActorManager.instance.player == __instance &&
                        Instance != null && Instance.antiRagdollSwim)
                    {
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Error("FallOver patch err: " + ex.Message);
                }
                return true;
            }
        }

        [HarmonyPatch]
        private class Patch_FpsStartRagdoll
        {
            static bool Prepare()
            {
                try { return AccessTools.Method(typeof(FpsActorController), "StartRagdoll") != null; }
                catch { return false; }
            }

            static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(FpsActorController), "StartRagdoll");
            }

            static bool Prefix(FpsActorController __instance) { return true; }

            static void Postfix(FpsActorController __instance)
            {
                try
                {
                    if (Instance == null || !Instance.antiRagdollSwim || __instance == null) return;
                    Actor player = ActorManager.instance != null ? ActorManager.instance.player : null;
                    if (player == null || player.controller != __instance) return;

                    if (player.ragdoll != null && isRagdollMI != null && stopRagdollMI != null)
                    {
                        bool rag = (bool)isRagdollMI.Invoke(player.ragdoll, null);
                        if (rag) stopRagdollMI.Invoke(player.ragdoll, null);
                    }

                    if (fallenOverFI != null) fallenOverFI.SetValue(player, false);

                    SafeCall(endRagdollMI, __instance, null);
                    Instance.EnablePlayerInput(player, true);
                }
                catch (Exception ex)
                {
                    MelonLogger.Error("StartRagdoll postfix err: " + ex.Message);
                }
            }
        }

        [HarmonyPatch]
        private class Patch_SwimInput
        {
            static bool Prepare()
            {
                try { return AccessTools.Method(typeof(FpsActorController), "SwimInput") != null; }
                catch { return false; }
            }

            static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(FpsActorController), "SwimInput");
            }

            static bool Prefix(FpsActorController __instance, ref Vector3 __result)
            {
                try
                {
                    if (__instance != null && ActorManager.instance != null &&
                        ActorManager.instance.player != null &&
                        ActorManager.instance.player.controller == __instance &&
                        Instance != null && Instance.antiRagdollSwim)
                    {
                        __result = Vector3.zero;
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Error("SwimInput prefix err: " + ex.Message);
                }
                return true;
            }

            static void Postfix(FpsActorController __instance, ref Vector3 __result)
            {
                try
                {
                    if (__instance != null && ActorManager.instance != null &&
                        ActorManager.instance.player != null &&
                        ActorManager.instance.player.controller == __instance &&
                        Instance != null && !Instance.antiRagdollSwim && Instance.swimSpeed != 30f && __result != Vector3.zero)
                    {
                        __result = __result.normalized * Instance.swimSpeed;
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Error("SwimInput postfix err: " + ex.Message);
                }
            }
        }

        [HarmonyPatch(typeof(Actor), "ApplyRecoil")]
        private static class Patch_NoRecoil_Actor
        {
            static bool Prefix(Actor __instance, ref Vector3 impulse)
            {
                if (Instance == null || !Instance.noRecoil) return true;
                Actor p = ActorManager.instance != null ? ActorManager.instance.player : null;
                if (__instance != p) return true;
                impulse = Vector3.zero;
                return false;
            }
        }

        [HarmonyPatch(typeof(Weapon), "CanFire")]
        private static class Patch_FullAuto_CanFire
        {
            static void Postfix(Weapon __instance, ref bool __result)
            {
                if (Instance == null || !Instance.fullAuto) return;
                Actor p = ActorManager.instance != null ? ActorManager.instance.player : null;
                if (p == null || p.activeWeapon != __instance) return;

                if (__instance.unholstered && !__instance.reloading &&
                    __instance.HasLoadedAmmo() && !__instance.CoolingDown())
                {
                    __result = true;
                }
            }
        }

        [HarmonyPatch(typeof(FpsActorController), "OnGround")]
        private static class Patch_InfiniteJump_OnGround
        {
            static void Postfix(FpsActorController __instance, ref bool __result)
            {
                if (Instance == null || !Instance.infiniteJump) return;
                Actor p = ActorManager.instance != null ? ActorManager.instance.player : null;
                if (p == null || p.controller != __instance) return;
                __result = true;
            }
        }
    }
}
