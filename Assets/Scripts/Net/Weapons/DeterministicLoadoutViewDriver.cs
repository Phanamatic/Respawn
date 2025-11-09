using Unity.Netcode;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

namespace Game.Net.Weapons
{
    /// <summary>
    /// Client-only view builder that listens to LoadoutSnapshotSync.State and
    /// attaches the correct local view prefab to the hand mount. It is idempotent
    /// and keyed by (weaponId, version) to avoid stale spawns.
    /// </summary>
    public sealed class DeterministicLoadoutViewDriver : MonoBehaviour
    {
        [Header("Required")]
        [SerializeField] Transform handMount;

        [Header("View Prefabs (Local-only)")]
        [Tooltip("Index 0 must be None. Map ids to prefabs by array index.")]
        [SerializeField] GameObject[] primaryViewPrefabs;
        [SerializeField] GameObject[] secondaryViewPrefabs;
        [SerializeField] GameObject[] meleeViewPrefabs;
        [SerializeField] GameObject[] utilityViewPrefabs;

        [Header("Optional HUD Hooks (kept always-enabled)")]
        [SerializeField] TMP_Text primaryName;
        [SerializeField] TMP_Text primaryAmmo;
        [SerializeField] Image primaryReloadFill;
        [SerializeField] TMP_Text secondaryName;
        [SerializeField] TMP_Text secondaryAmmo;
        [SerializeField] Image secondaryReloadFill;

        LoadoutSnapshotSync _sync;
        ushort _lastAppliedVersion;
        byte _lastActiveSlot;
        byte _lastPrimaryId, _lastSecondaryId, _lastMeleeId, _lastUtilityId;

        void Awake()
        {
            // Get the owning player's sync. This script sits on the Player object.
            _sync = GetComponent<LoadoutSnapshotSync>();
        }

        void OnEnable()
        {
            if (_sync)
                _sync.State.OnValueChanged += OnSnapshotChanged;
        }

        void OnDisable()
        {
            if (_sync)
                _sync.State.OnValueChanged -= OnSnapshotChanged;
        }

        void Start()
        {
            // Apply initial state if we joined late.
            if (_sync) OnSnapshotChanged(default, _sync.State.Value);
        }

        void OnSnapshotChanged(LoadoutSnapshotSync.Snapshot oldSnap, LoadoutSnapshotSync.Snapshot newSnap)
        {
            // Ignore out-of-order / duplicate updates.
            if (newSnap.version == _lastAppliedVersion &&
                newSnap.activeSlot == _lastActiveSlot &&
                newSnap.primaryId == _lastPrimaryId &&
                newSnap.secondaryId == _lastSecondaryId &&
                newSnap.meleeId == _lastMeleeId &&
                newSnap.utilityId == _lastUtilityId)
                return;

            _lastAppliedVersion = newSnap.version;
            _lastActiveSlot = newSnap.activeSlot;
            _lastPrimaryId = newSnap.primaryId;
            _lastSecondaryId = newSnap.secondaryId;
            _lastMeleeId = newSnap.meleeId;
            _lastUtilityId = newSnap.utilityId;

            // 1) Hand-mount: nuke stale children, then spawn the correct local view for the active slot.
            if (handMount)
            {
                for (int i = handMount.childCount - 1; i >= 0; i--)
                    Destroy(handMount.GetChild(i).gameObject);

                GameObject viewPrefab = null;
                switch (newSnap.activeSlot)
                {
                    case 0: viewPrefab = SafeGet(primaryViewPrefabs, newSnap.primaryId); break;
                    case 1: viewPrefab = SafeGet(secondaryViewPrefabs, newSnap.secondaryId); break;
                    case 2: viewPrefab = SafeGet(meleeViewPrefabs, newSnap.meleeId); break;
                    case 3: viewPrefab = SafeGet(utilityViewPrefabs, newSnap.utilityId); break;
                }
                if (viewPrefab) Instantiate(viewPrefab, handMount, false);
            }

            // 2) HUD: update without disabling components.
            if (primaryName)     primaryName.text = newSnap.primaryName.ToString();
            if (secondaryName)   secondaryName.text = newSnap.secondaryName.ToString();

            if (primaryAmmo)
            {
                string reserve = newSnap.primaryReserve < 0 ? "INF" : Mathf.Max(0, newSnap.primaryReserve).ToString("D3");
                primaryAmmo.text = $"{Mathf.Max(0, newSnap.primaryMag):D3}/{reserve}";
            }
            if (secondaryAmmo)
            {
                string reserve = newSnap.secondaryReserve < 0 ? "INF" : Mathf.Max(0, newSnap.secondaryReserve).ToString("D3");
                secondaryAmmo.text = $"{Mathf.Max(0, newSnap.secondaryMag):D3}/{reserve}";
            }
            if (primaryReloadFill)
            {
                primaryReloadFill.fillAmount = newSnap.isReloadingPrimary ? Mathf.Clamp01(newSnap.reloadProgressPrimary) : 0f;
                var c = primaryReloadFill.color; c.a = newSnap.isReloadingPrimary ? 1f : 0f; primaryReloadFill.color = c;
            }
            if (secondaryReloadFill)
            {
                secondaryReloadFill.fillAmount = newSnap.isReloadingSecondary ? Mathf.Clamp01(newSnap.reloadProgressSecondary) : 0f;
                var c = secondaryReloadFill.color; c.a = newSnap.isReloadingSecondary ? 1f : 0f; secondaryReloadFill.color = c;
            }
        }

        static GameObject SafeGet(GameObject[] arr, int id)
        {
            if (arr == null || id < 0 || id >= arr.Length) return null;
            return arr[id];
        }
    }
}