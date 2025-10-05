// Assets/Scripts/Networking/Runtime/UI/ProximityBillboardText.cs
using UnityEngine;
using Unity.Netcode;
using TMPro;

[DisallowMultipleComponent]
[RequireComponent(typeof(TextMeshPro))]
public sealed class ProximityBillboardText : MonoBehaviour
{
    [Header("Proximity")]
    [SerializeField, Min(0.1f)] float triggerRadius = 6f;
    [SerializeField, Min(0.01f)] float openDuration = 0.25f;
    [SerializeField, Min(0.01f)] float closeDuration = 0.25f;

    [Header("Spin While Opening")]
    [SerializeField] float spinSpeedOpen = 360f;

    [Header("Billboard")]
    [SerializeField] bool faceCamera = true;
    [SerializeField] bool yOnly = true;

    [Header("Orientation Fix")]
    [Tooltip("Flip 180° so text is not mirrored/backwards when facing camera.")]
    [SerializeField] bool flipFacing180 = true;

    [Header("Target (optional)")]
    [SerializeField] Transform target;

    [Header("Visibility")]
    [Tooltip("If true and this object is under a player NetworkObject, only that local owner sees the billboard.")]
    [SerializeField] bool showOnlyForOwner = false;   // world billboards default to visible for any client in radius

    TextMeshPro _tmp;
    float _t;                 // 0..1 open state
    bool _open;               // desired state
    float _spin;              // degrees while opening
    Camera _cam;
    float _baseFontSize;      // original TMP size = 100%
    NetworkObject _ownerNO;   // parent/root NO used to decide owner-visibility

    static readonly Quaternion kFlipY180 = Quaternion.Euler(0f, 180f, 0f);

    void Awake()
    {
        // Cache components early. Do not decide visibility yet because NGO ownership isn't ready.
        _tmp = GetComponent<TextMeshPro>();
        _cam = Camera.main;
        _baseFontSize = Mathf.Max(1f, _tmp.fontSize);
        _t = 0f;
        // Start fully closed and hidden
        _tmp.fontSize = 0f;
        _tmp.enabled = false;
        transform.localScale = Vector3.zero;
        // Hide any renderers under this root to be safe on all TMP variants
        var rs = GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < rs.Length; i++) rs[i].enabled = false;

        _ownerNO = GetComponentInParent<NetworkObject>();
        // Billboard opens/closes per-client; no network authority needed here.
    }

    void Start()
    {
        // If attached under a player object and owner-only is set, show only for that local owner.
        bool gateToOwner = false;
        if (showOnlyForOwner && _ownerNO != null && _ownerNO.IsPlayerObject)
        {
            var pn = _ownerNO.GetComponent<Game.Net.PlayerNetwork>();
            gateToOwner = pn != null && !pn.IsOwner;
        }
        else
        {
            gateToOwner = false; // never gate world objects
        }

        if (gateToOwner)
        {
            if (_tmp) _tmp.enabled = false;
            enabled = false;
            return;
        }

        if (_tmp) _tmp.enabled = true;
    }

    void LateUpdate()
    {
        // runs locally on each client; no NGO dependency

        if (!target) target = ResolveLocalPlayerTransform();
        if (!_cam)
        {
            _cam = Camera.main;
#if UNITY_2022_3_OR_NEWER || UNITY_6000_0_OR_NEWER
            if (!_cam) _cam = FindFirstObjectByType<Camera>(FindObjectsInactive.Exclude);
#else
            if (!_cam) _cam = FindObjectOfType<Camera>();
#endif
        }

        float dist = target ? Vector3.Distance(target.position, transform.position) : float.MaxValue;
        _open = dist <= triggerRadius;

        // Per-client show/hide gate
        if (_open && (_tmp == null || !_tmp.enabled))
        {
            if (_tmp) _tmp.enabled = true;
            // enable renderers when starting to open
            var ren = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < ren.Length; i++) ren[i].enabled = true;
        }

        // Animate open/close
        float dt = Time.unscaledDeltaTime;
        float dur = _open ? Mathf.Max(0.001f, openDuration) : Mathf.Max(0.001f, closeDuration);
        float dir = _open ? 1f : -1f;
        float tPrev = _t;
        _t = Mathf.Clamp01(_t + dir * (dt / dur));

        // Apply 0%→100% scale
        transform.localScale = Vector3.one * _t;

        // Fully close → hard hide so outsiders never see it
        if (!_open && _t <= 0f)
        {
            if (_tmp) _tmp.enabled = false;
            var ren = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < ren.Length; i++) ren[i].enabled = false;
        }

        // Spin only while opening
        bool isOpeningNow = dir > 0f && _t > 0f && _t < 1f && _t >= tPrev;
        if (isOpeningNow) _spin += spinSpeedOpen * dt;
        else _spin = 0f;

        // Face local camera; no networked state is changed
        if (faceCamera && _cam)
        {
            Quaternion look;
            if (yOnly)
            {
                var toCam = _cam.transform.position - transform.position;
                toCam.y = 0f;
                if (toCam.sqrMagnitude < 1e-6f) toCam = transform.forward;
                look = Quaternion.LookRotation(toCam.normalized, Vector3.up);
            }
            else
            {
                var dirToCam = (_cam.transform.position - transform.position).normalized;
                look = Quaternion.LookRotation(dirToCam, Vector3.up);
            }

            if (flipFacing180) look *= kFlipY180;

            transform.rotation = isOpeningNow
                ? look * Quaternion.AngleAxis(_spin, Vector3.up)
                : look;
        }
    }

    Transform ResolveLocalPlayerTransform()
    {
        // Prefer local owner PlayerNetwork if present.
        var players = FindObjectsByType<Game.Net.PlayerNetwork>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < players.Length; i++)
            if (players[i] && players[i].IsOwner) return players[i].transform;

        // Fallbacks for non-player-attached billboards.
        var nm = NetworkManager.Singleton;
        var po = nm ? nm.LocalClient?.PlayerObject : null;
        if (po) return po.transform;

        var tagged = GameObject.FindGameObjectWithTag("Player");
        return tagged ? tagged.transform : null;
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        triggerRadius = Mathf.Max(0.1f, triggerRadius);
        openDuration = Mathf.Max(0.01f, openDuration);
        closeDuration = Mathf.Max(0.01f, closeDuration);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.5f);
        Gizmos.DrawWireSphere(transform.position, triggerRadius);
    }
#endif
}
