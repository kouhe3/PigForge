using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PigForge.UnityReference
{
public sealed class ReferenceReplayExporter : MonoBehaviour
{
    [SerializeField] private int fixedTickRate = 60;
    [SerializeField] private int simulationTicks = 120;
    [SerializeField] private uint randomSeed = 1;
    [SerializeField] private string contentVersion = "reference-content-v1";
    [SerializeField] private string physicsBehaviorVersion = "unity-6000-physx-reference-v1";
    [SerializeField] private string outputFileName = "unity-reference-replay.json";
    [SerializeField] private bool exportOnStart = true;

    private readonly List<ReplayFrame> frames = new();
    private readonly List<ReplayEvent> tickEvents = new();
    private Scene replayScene;
    private PhysicsScene replayPhysicsScene;
    private Scene previousActiveScene;
    private bool replaySceneCreated;
    private GameObject ground;
    private GameObject box;
    private Rigidbody boxBody;

    private void Start()
    {
        if (exportOnStart)
            ExportSimpleScene();
    }

    [ContextMenu("Export Simple Replay")]
    public void ExportSimpleScene()
    {
        if (!Application.isPlaying)
            throw new InvalidOperationException("ExportSimpleScene must be run in Play Mode so OnCollisionEnter/Stay/Exit collision events are available.");
        ValidateSettings();

        SimulationMode previousSimulationMode = Physics.simulationMode;
        previousActiveScene = SceneManager.GetActiveScene();
        Physics.simulationMode = SimulationMode.Script;

        try
        {
            CreateScene();
            ReplayEntity[] initialEntities = CaptureEntities();
            frames.Clear();

            float deltaTime = 1f / fixedTickRate;
            for (int tick = 1; tick <= simulationTicks; tick++)
            {
                tickEvents.Clear();
                replayPhysicsScene.Simulate(deltaTime);

                SortEvents();
                frames.Add(new ReplayFrame
                {
                    tick = (uint)tick,
                    snapshots = CaptureEntities(),
                    events = tickEvents.ToArray()
                });
            }

            ReplayEntity[] finalEntities = CaptureEntities();
            string stateHash = ComputeStateHash((uint)simulationTicks, finalEntities);
            var document = new ReplayDocument
            {
                format = "pigforge.physics.replay",
                header = new ReplayHeader
                {
                    protocolVersion = 1,
                    contentVersion = contentVersion,
                    physicsBehaviorVersion = physicsBehaviorVersion,
                    stateHashAlgorithm = "sha256-canonical-v1",
                    fixedTickRate = fixedTickRate,
                    simulationTicks = simulationTicks,
                    randomSeed = randomSeed
                },
                initialState = new ReplayInitialState
                {
                    entities = initialEntities,
                    joints = Array.Empty<ReplayJoint>()
                },
                commands = Array.Empty<ReplayCommand>(),
                frames = frames.ToArray(),
                finalResult = new ReplayResult
                {
                    outcome = "SUCCESS",
                    completedTick = (uint)simulationTicks,
                    stateHash = stateHash
                }
            };

            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "replays", outputFileName));
            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("The replay output directory is invalid.");

            Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonUtility.ToJson(document, true), new UTF8Encoding(false));
            Debug.Log($"[PigForge] Unity reference replay exported: {path}");
        }
        finally
        {
            try
            {
                DestroyScene();
            }
            finally
            {
                try
                {
                    if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
                        SceneManager.SetActiveScene(previousActiveScene);
                }
                finally
                {
                    Physics.simulationMode = previousSimulationMode;
                }
            }
        }
    }

    private void ValidateSettings()
    {
        if (fixedTickRate <= 0 || fixedTickRate > 240)
            throw new InvalidOperationException("fixedTickRate must be between 1 and 240.");
        if (simulationTicks <= 0 || simulationTicks > 100000)
            throw new InvalidOperationException("simulationTicks must be between 1 and 100000.");
        ValidateVersion(contentVersion, nameof(contentVersion));
        ValidateVersion(physicsBehaviorVersion, nameof(physicsBehaviorVersion));
        if (string.IsNullOrWhiteSpace(outputFileName) || Path.GetFileName(outputFileName) != outputFileName)
            throw new InvalidOperationException("outputFileName must be a non-empty file name without path components.");
        foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
        {
            if (outputFileName.IndexOf(invalidCharacter) >= 0)
                throw new InvalidOperationException("outputFileName contains an invalid file name character.");
        }
    }

    private static void ValidateVersion(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            throw new InvalidOperationException(fieldName + " must contain 1 to 128 non-whitespace characters.");
    }

    private void CreateScene()
    {
        const string baseSceneName = "ReferenceReplay";
        string sceneName = baseSceneName;
        int suffix = 1;
        while (SceneManager.GetSceneByName(sceneName).IsValid())
            sceneName = baseSceneName + "-" + suffix++;

        replayScene = SceneManager.CreateScene(
            sceneName,
            new CreateSceneParameters(LocalPhysicsMode.Physics3D));
        replaySceneCreated = true;
        replayPhysicsScene = replayScene.GetPhysicsScene();
        if (!replayPhysicsScene.IsValid())
            throw new InvalidOperationException("The isolated replay physics scene is invalid.");
        SceneManager.SetActiveScene(replayScene);

        ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.name = "ReferenceGround";
        ground.transform.SetPositionAndRotation(new Vector3(0f, -0.5f, 0f), Quaternion.identity);
        ground.transform.localScale = new Vector3(10f, 1f, 10f);
        AddBodyMetadata(ground, 1, true);

        box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.name = "ReferenceBox";
        box.transform.SetPositionAndRotation(new Vector3(0f, 3f, 0f), Quaternion.identity);
        boxBody = AddBodyMetadata(box, 2, false);
        boxBody.useGravity = true;
        boxBody.linearDamping = 0f;
        boxBody.angularDamping = 0.05f;
    }

    private Rigidbody AddBodyMetadata(GameObject target, uint bodyId, bool isGround)
    {
        Rigidbody body = target.AddComponent<Rigidbody>();
        body.isKinematic = isGround;
        body.useGravity = !isGround;
        if (isGround)
            body.constraints = RigidbodyConstraints.FreezeAll;

        var metadata = target.AddComponent<ReplayBody>();
        metadata.bodyId = bodyId;
        metadata.entityId = bodyId;
        metadata.partTypeId = 1;

        var recorder = target.AddComponent<CollisionRecorder>();
        recorder.owner = this;
        recorder.body = metadata;
        return body;
    }

    private ReplayEntity[] CaptureEntities()
    {
        return new[] { CaptureEntity(ground), CaptureEntity(box) };
    }

    private static ReplayEntity CaptureEntity(GameObject target)
    {
        if (target == null)
            throw new InvalidOperationException("Cannot capture a destroyed replay entity.");

        ReplayBody metadata = target.GetComponent<ReplayBody>();
        Rigidbody body = target.GetComponent<Rigidbody>();
        if (metadata == null || body == null || metadata.entityId == 0 || metadata.bodyId == 0 || metadata.partTypeId == 0)
            throw new InvalidOperationException("Replay entities require stable positive IDs and a Rigidbody.");

        Vector3 position = target.transform.position;
        Quaternion rotation = target.transform.rotation;
        Vector3 linearVelocity = body.linearVelocity;
        Vector3 angularVelocity = body.angularVelocity;
        EnsureFinite(position, rotation, linearVelocity, angularVelocity);
        return new ReplayEntity
        {
            entityId = metadata.entityId,
            physicsBodyId = metadata.bodyId,
            partTypeId = metadata.partTypeId,
            position = new[] { position.x, position.y, position.z },
            rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w },
            linearVelocity = new[] { linearVelocity.x, linearVelocity.y, linearVelocity.z },
            angularVelocity = new[] { angularVelocity.x, angularVelocity.y, angularVelocity.z }
        };
    }

    private static void EnsureFinite(Vector3 position, Quaternion rotation, Vector3 linearVelocity, Vector3 angularVelocity)
    {
        if (!IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z) ||
            !IsFinite(rotation.x) || !IsFinite(rotation.y) || !IsFinite(rotation.z) || !IsFinite(rotation.w) ||
            !IsFinite(linearVelocity.x) || !IsFinite(linearVelocity.y) || !IsFinite(linearVelocity.z) ||
            !IsFinite(angularVelocity.x) || !IsFinite(angularVelocity.y) || !IsFinite(angularVelocity.z))
            throw new InvalidOperationException("Replay state contains a non-finite numeric value.");
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private void RecordContact(string kind, ReplayBody other, ReplayBody self)
    {
        if (other == null || self == null || other.bodyId == 0 || self.bodyId == 0 || self.bodyId > other.bodyId)
            return;
        tickEvents.Add(new ReplayEvent
        {
            kind = kind,
            bodyA = self.bodyId,
            bodyB = other.bodyId
        });
    }

    private void SortEvents()
    {
        tickEvents.Sort((left, right) =>
        {
            int kindComparison = string.CompareOrdinal(left.kind, right.kind);
            if (kindComparison != 0)
                return kindComparison;
            int bodyComparison = left.bodyA.CompareTo(right.bodyA);
            return bodyComparison != 0 ? bodyComparison : left.bodyB.CompareTo(right.bodyB);
        });
    }

    private static string ComputeStateHash(uint completedTick, ReplayEntity[] entities)
    {
        if (entities == null)
            throw new ArgumentNullException(nameof(entities));

        ReplayEntity[] orderedEntities = (ReplayEntity[])entities.Clone();
        Array.Sort(orderedEntities, (left, right) => left.entityId.CompareTo(right.entityId));
        using var stream = new MemoryStream();
        byte[] prefix = Encoding.ASCII.GetBytes("pigforge.state.v1\0");
        stream.Write(prefix, 0, prefix.Length);
        WriteUInt32LittleEndian(stream, completedTick);
        WriteUInt32LittleEndian(stream, checked((uint)orderedEntities.Length));

        foreach (ReplayEntity entity in orderedEntities)
        {
            if (entity == null || entity.entityId == 0 || entity.physicsBodyId == 0 || entity.partTypeId == 0)
                throw new InvalidOperationException("State hash cannot include an entity with a non-positive ID.");
            WriteUInt32LittleEndian(stream, entity.entityId);
            WriteUInt32LittleEndian(stream, entity.physicsBodyId);
            WriteUInt32LittleEndian(stream, entity.partTypeId);
            WriteVector3(stream, entity.position);
            WriteQuaternion(stream, entity.rotation);
            WriteVector3(stream, entity.linearVelocity);
            WriteVector3(stream, entity.angularVelocity);
        }

        using SHA256 sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(stream.ToArray());
        return BitConverter.ToString(digest).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static void WriteVector3(Stream stream, float[] value)
    {
        if (value == null || value.Length != 3)
            throw new InvalidOperationException("Canonical vector3 values must contain exactly three elements.");
        for (int index = 0; index < value.Length; index++)
            WriteSingleLittleEndian(stream, value[index]);
    }

    private static void WriteQuaternion(Stream stream, float[] value)
    {
        if (value == null || value.Length != 4)
            throw new InvalidOperationException("Canonical quaternion values must contain exactly four elements.");
        for (int index = 0; index < value.Length; index++)
            WriteSingleLittleEndian(stream, value[index]);
    }

    private static void WriteSingleLittleEndian(Stream stream, float value)
    {
        if (!IsFinite(value))
            throw new InvalidOperationException("Canonical state hash cannot serialize a non-finite value.");
        WriteUInt32LittleEndian(stream, unchecked((uint)BitConverter.SingleToInt32Bits(value)));
    }

    private static void WriteUInt32LittleEndian(Stream stream, uint value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 24));
    }

    private void DestroyScene()
    {
        DestroyObject(ground);
        DestroyObject(box);
        ground = null;
        box = null;
        boxBody = null;

        if (replaySceneCreated && replayScene.IsValid() && replayScene.isLoaded)
            SceneManager.UnloadSceneAsync(replayScene);
        replaySceneCreated = false;
        replayScene = default;
        replayPhysicsScene = default;
    }

    private static void DestroyObject(UnityEngine.Object target)
    {
        if (target == null)
            return;
        if (Application.isPlaying)
            UnityEngine.Object.Destroy(target);
        else
            UnityEngine.Object.DestroyImmediate(target);
    }

    [Serializable] private sealed class ReplayBody : MonoBehaviour { public uint entityId; public uint bodyId; public uint partTypeId; }

    private sealed class CollisionRecorder : MonoBehaviour
    {
        public ReferenceReplayExporter owner;
        public ReplayBody body;

        private void OnCollisionEnter(Collision collision)
        {
            owner.RecordContact("CONTACT_STARTED", collision.collider.GetComponentInParent<ReplayBody>(), body);
        }

        private void OnCollisionStay(Collision collision)
        {
            owner.RecordContact("CONTACT_PERSISTED", collision.collider.GetComponentInParent<ReplayBody>(), body);
        }

        private void OnCollisionExit(Collision collision)
        {
            owner.RecordContact("CONTACT_ENDED", collision.collider.GetComponentInParent<ReplayBody>(), body);
        }
    }

    [Serializable] private sealed class ReplayDocument { public string format; public ReplayHeader header; public ReplayInitialState initialState; public ReplayCommand[] commands; public ReplayFrame[] frames; public ReplayResult finalResult; }
    [Serializable] private sealed class ReplayHeader { public int protocolVersion; public string contentVersion; public string physicsBehaviorVersion; public string stateHashAlgorithm; public int fixedTickRate; public int simulationTicks; public uint randomSeed; }
    [Serializable] private sealed class ReplayInitialState { public ReplayEntity[] entities; public ReplayJoint[] joints; }
    [Serializable] private sealed class ReplayCommand { }
    [Serializable] private sealed class ReplayJoint { }
    [Serializable] private sealed class ReplayFrame { public uint tick; public ReplayEntity[] snapshots; public ReplayEvent[] events; }
    [Serializable] private sealed class ReplayEvent { public string kind; public uint bodyA; public uint bodyB; }
    [Serializable] private sealed class ReplayResult { public string outcome; public uint completedTick; public string stateHash; }
    [Serializable] private sealed class ReplayEntity { public uint entityId; public uint physicsBodyId; public uint partTypeId; public float[] position; public float[] rotation; public float[] linearVelocity; public float[] angularVelocity; }
}
}
