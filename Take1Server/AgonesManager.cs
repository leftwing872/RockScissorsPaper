using Agones;
using Agones.Dev.Sdk;
using Grpc.Core;

/// <summary>
/// Thin wrapper around the Agones C# SDK.
///
/// Lifecycle on an Agones-managed fleet:
///   1. The binary is launched inside the GameServer Pod alongside the Agones sidecar.
///   2. The process calls <see cref="Ready"/> once it can accept players. Agones then
///      moves the GameServer to the "Ready" state and starts routing allocations to it.
///   3. A background timer calls Health() on an interval so Agones knows the process is
///      alive and does not restart the Pod.
///   4. When a match actually begins we call <see cref="Allocate"/> to self-allocate
///      (useful when not using a separate allocator/matchmaker).
///   5. When the match ends we call <see cref="Shutdown"/> so Agones can recycle the Pod.
///
/// All gRPC calls target the local sidecar, so they are cheap and only fail when the
/// sidecar is missing (e.g. running locally without Agones). Failures are logged and
/// swallowed so the game server keeps running in non-Agones environments.
/// </summary>
class AgonesManager : IDisposable
{
    // Agones GameServer health defaults to periodSeconds=5, failureThreshold=3.
    // Pinging every 2s leaves plenty of margin against a missed beat.
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(2);

    private readonly AgonesSDK? sdk;
    private readonly Action<string> log;
    private Timer? healthTimer;
    private bool disposed;

    /// <summary>
    /// True when the SDK was successfully constructed and we are expected to be running
    /// under an Agones sidecar. When false every call becomes a no-op.
    /// </summary>
    public bool Enabled { get; private set; }

    /// <param name="log">Logging callback (reuses the server's writeLog).</param>
    /// <param name="enabled">
    /// Set to false to disable Agones entirely (e.g. local development). When omitted it
    /// is driven by the AGONES_SDK_GRPC_PORT environment variable that the sidecar injects.
    /// </param>
    public AgonesManager(Action<string> log, bool? enabled = null)
    {
        this.log = log;

        bool runUnderAgones = enabled
            ?? !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AGONES_SDK_GRPC_PORT"));

        if (!runUnderAgones)
        {
            log("Agones: sidecar not detected (AGONES_SDK_GRPC_PORT unset). Running without Agones.");
            Enabled = false;
            return;
        }

        try
        {
            // The default constructor reads AGONES_SDK_GRPC_HOST / AGONES_SDK_GRPC_PORT
            // to reach the sidecar over the loopback gRPC endpoint.
            sdk = new AgonesSDK();
            Enabled = true;
            log("Agones: SDK initialized.");

            // This SDK version does not ping health automatically, so drive it ourselves.
            healthTimer = new Timer(_ => Call("Health", () => sdk!.HealthAsync(), logSuccess: false),
                null, HealthInterval, HealthInterval);
        }
        catch (Exception ex)
        {
            log("Agones: failed to initialize SDK. Continuing without Agones. " + ex.Message);
            Enabled = false;
        }
    }

    /// <summary>Marks the GameServer as Ready so Agones can allocate players to it.</summary>
    public void Ready() => Call("Ready", () => sdk!.ReadyAsync());

    /// <summary>Self-allocates the GameServer (moves it to Allocated, protecting it from scale-down).</summary>
    public void Allocate() => Call("Allocate", () => sdk!.AllocateAsync());

    /// <summary>Tells Agones the GameServer has finished and the Pod can be shut down.</summary>
    public void Shutdown() => Call("Shutdown", () => sdk!.ShutDownAsync());

    /// <summary>Sets a label on the GameServer resource (visible to allocators/matchmakers).</summary>
    public void SetLabel(string key, string value) => Call("SetLabel", () => sdk!.SetLabelAsync(key, value));

    /// <summary>Sets an annotation on the GameServer resource.</summary>
    public void SetAnnotation(string key, string value) => Call("SetAnnotation", () => sdk!.SetAnnotationAsync(key, value));

    /// <summary>Subscribes to GameServer state changes pushed from the sidecar.</summary>
    public void WatchGameServer(Action<GameServer> onUpdate)
    {
        if (!Enabled || sdk == null)
        {
            return;
        }

        try
        {
            sdk.WatchGameServer(gs =>
            {
                try { onUpdate(gs); }
                catch (Exception ex) { log("Agones: WatchGameServer callback error. " + ex.Message); }
            });
            log("Agones: watching GameServer updates.");
        }
        catch (Exception ex)
        {
            log("Agones: WatchGameServer failed. " + ex.Message);
        }
    }

    // Runs an SDK gRPC call synchronously and logs the outcome. Errors never propagate so
    // a missing sidecar can't crash the game loop.
    private void Call(string name, Func<Task<Status>> action, bool logSuccess = true)
    {
        if (!Enabled || sdk == null)
        {
            return;
        }

        try
        {
            Status status = action().GetAwaiter().GetResult();
            if (status.StatusCode == StatusCode.OK)
            {
                if (logSuccess)
                {
                    log("Agones: " + name + " OK");
                }
            }
            else
            {
                log("Agones: " + name + " failed - " + status.StatusCode + " " + status.Detail);
            }
        }
        catch (Exception ex)
        {
            log("Agones: " + name + " threw - " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        try { healthTimer?.Dispose(); } catch { }
        try { sdk?.Dispose(); } catch { }
    }
}
