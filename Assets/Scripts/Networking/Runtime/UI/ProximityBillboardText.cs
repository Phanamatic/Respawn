// Assets/Scripts/Networking/Runtime/UI/ProximityBillboardText.cs
using UnityEngine;
using Unity.Netcode;
using TMPro;

[DisallowMultipleComponent]
[RequireComponent(typeof(TextMeshPro))]
public sealed class ProximityBillboardText : NetworkBehaviour
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
    [SerializeField] bool showOnlyForOwner = true;

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
        _tmp.fontSize = 0f; // start closed
        _ownerNO = GetComponentInParent<NetworkObject>();
    }

    public override void OnNetworkSpawn()
    {
        // Run only on clients. Dedicated server and server-logic never show or tick this.
        if (!IsClient)
        {
            if (_tmp) _tmp.enabled = false;
            enabled = false;
            return;
        }

        // Owner-gated visibility (common for player-attached billboards like "Press E").
        if (showOnlyForOwner && _ownerNO != null && !_ownerNO.IsOwner)
        {
            if (_tmp) _tmp.enabled = false;
            enabled = false; // fully disable updates so no transform churn on remotes
            return;
        }

        // Ensure visible component is enabled for the local viewer.
        if (_tmp) _tmp.enabled = true;
    }

    void LateUpdate()
    {
        if (!IsClient) return; // extra safety on host-mode state changes

        if (!target) target = ResolveLocalPlayerTransform();
        if (!_cam) _cam = Camera.main;

        float dist = target ? Vector3.Distance(target.position, transform.position) : float.MaxValue;
        _open = dist <= triggerRadius;

        float dt = Time.unscaledDeltaTime;
        float dur = _open ? Mathf.Max(0.001f, openDuration) : Mathf.Max(0.001f, closeDuration);
        float dir = _open ? 1f : -1f;
        float tPrev = _t;
        _t = Mathf.Clamp01(_t + dir * (dt / dur));

        // Smoothstep easing
        float ease = _t * _t * (3f - 2f * _t);

        // Scale font from 0% to 100% locally only
        _tmp.fontSize = Mathf.LerpUnclamped(0f, _baseFontSize, ease);

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
