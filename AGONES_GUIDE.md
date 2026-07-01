# Agones 연동 가이드 — RockScissorsPaper Server

이 문서는 현재 GameLift 기반(주석 처리된 상태)으로 작성된 `Take1Server`를
**Agones**(Kubernetes 기반 전용 게임서버 오케스트레이터)로 옮기기 위한
개념 설명 + 단계별 연동 가이드입니다.

---

## 0. AI 에이전트용 컨텍스트 (먼저 읽을 것)

> 이 문서는 사람이 읽는 설명서이자, **AI 에이전트가 단독으로 구현을 수행하기 위한 작업 지시서**입니다.
> 아래 "소스맵"과 각 섹션의 코드/위치 표시를 그대로 따르면 됩니다.

### 대상 프로젝트 소스맵

| 파일 | 역할 | 이번 작업에서 |
|---|---|---|
| `Take1Server/Program.cs` | 엔트리포인트 (`new ChatServer().Start()`) | `await` 진입점으로 변경 |
| `Take1Server/ChatServer.cs` | TCP 서버 본체, 게임 로직, MsgCode enum | Agones 호출 삽입 (3곳) |
| `Take1Server/ChatClient.cs` | 접속 클라이언트 1명 표현 | 변경 없음 |
| `Take1Server/AWSCloudWatch.cs` | CloudWatch 로깅 (`acw.CloudWatchLog`) | 유지 (로그는 그대로) |
| `Take1Server/Take1Server.csproj` | 패키지/참조 | `AgonesSDK` 추가, GameLift 참조 제거 |
| `Take1Server/AgonesManager.cs` | **(신규)** SDK 래퍼 | 새로 생성 |
| `Take1Server/Dockerfile` | **(신규)** 컨테이너 이미지 | 새로 생성 |
| `*.yaml` | **(신규)** GameServer / Fleet / Allocation | 새로 생성 |

### 현재 서버 동작 요약 (변경하면 안 되는 게임 로직)

- 포트 **7777** TCP, 한 서버당 **정확히 2명**(`MaxPlayers = 2`) 매치.
- 메시지는 줄 단위 텍스트, 맨 앞 **2자리 숫자**가 `MsgCode`(예: `40` = Play).
- 양쪽이 카드를 내면 승패 판정 → 승자에게 `50`(GameOver) 브로드캐스트 →
  `Thread.Sleep(2000)` → `Environment.Exit(0)` 로 **프로세스 자체가 종료**됨.
- 이 "한 판 끝나면 프로세스 종료" 모델은 Agones의 **GameServer=1프로세스=1매치** 모델과 잘 맞음.
  → 종료 직전에 `ShutdownAsync()` 한 줄만 끼우면 됨.

### 작업 불변식 (AI는 이걸 깨면 안 됨)

1. 게임 판정 로직(`(card0 % 3) + 1 == card1`)과 MsgCode 프로토콜은 **절대 수정 금지**.
2. 로컬(사이드카 없음)에서도 서버는 **죽지 않고** TCP만으로 돌아가야 함 → Agones 초기화 실패는 치명적 에러가 아님.
3. `Environment.Exit(0)` 직전에 반드시 `ShutdownAsync()`가 await 되도록 할 것.
4. 코드 Health 주기 < YAML `periodSeconds` 불변식 유지 (§6 참조).

---

## 1. Agones란?

Agones는 **쿠버네티스 위에서 전용 게임 서버를 띄우고 스케일링·할당·정리**해주는
오픈소스 플랫폼입니다 (Google Cloud + Ubisoft, 현재 CNCF 프로젝트).

- **GameLift** = AWS 매니지드 게임서버 호스팅 (클라우드 종속)
- **Agones** = 쿠버네티스에서 직접 돌리는 셀프호스팅 (EKS / GKE / 온프레미스 어디든)

### 핵심 리소스 4가지

| 리소스 | 역할 | RockScissorsPaper 대응 |
|---|---|---|
| **GameServer** | 게임 서버 프로세스 1개(=파드 1개) | 가위바위보 한 판을 처리하는 서버 |
| **Fleet** | 동일 GameServer N개 묶음(미리 워밍업) | "Ready 서버 N개 유지" |
| **GameServerAllocation** | Ready 서버 1개를 Allocated로 잡아줌 | 매치메이커가 빈 방 요청 |
| **SDK / 사이드카** | 각 파드에 붙는 gRPC 컨테이너(localhost:9357) | 서버 코드가 상태 보고 |

게임 서버 코드는 같은 파드에 자동으로 붙는 **사이드카 컨테이너**와 gRPC로 통신하며
자기 상태(Ready / Health / Shutdown)를 보고합니다.

---

## 2. 라이프사이클 — GameLift ↔ Agones 매핑

현재 `ChatServer.cs`에 주석으로 남아 있는 GameLift 호출을 1:1로 대응시키면:

| 단계 | GameLift (현재 주석) | Agones (C# SDK) |
|---|---|---|
| SDK 초기화 | `GameLiftServerAPI.InitSDK()` | `new AgonesSDK()` + `ConnectAsync()` |
| 준비 완료 보고 | `ProcessReady()` | `await sdk.ReadyAsync()` |
| 헬스체크 | `OnHealthCheck` 콜백 | 주기적 `await sdk.HealthAsync()` |
| 세션 시작 감지 | `OnStartGameSession` 콜백 | `sdk.WatchGameServer(cb)` → `Allocated` 감지 |
| 플레이어 검증 | `AcceptPlayerSession()` | (Alpha) `PlayerConnectAsync()` 또는 생략 |
| 세션 종료 | `ProcessEnding()` | `await sdk.ShutdownAsync()` |

**기본 흐름**
1. 서버 부팅 → 포트 리스닝 준비 완료 → `ReadyAsync()` 호출
2. 백그라운드에서 주기적으로 `HealthAsync()` 전송 (살아있음 증명)
3. 매치메이커가 할당 → 상태가 `Allocated`로 변경 (`WatchGameServer`로 감지 가능)
4. 게임 종료(현재 `GameOver` 직후 `Environment.Exit(0)` 자리) → `ShutdownAsync()` 호출

---

## 3. C# SDK 연동

### 3-1. 패키지 추가

`Take1Server.csproj`에 Agones C# SDK 추가:

```bash
cd Take1Server
dotnet add package AgonesSDK
```

> AgonesSDK NuGet 패키지는 내부적으로 gRPC(`Grpc.Net.Client`)를 사용합니다.
> 기존 GameLift `<Reference>`/`AWSSDK.GameLift`는 Agones로 완전히 옮기면 제거해도 됩니다.
> (CloudWatch 로깅을 유지하려면 `AWSSDK.CloudWatchLogs`는 남겨두세요.)

### 3-2. AgonesManager 래퍼 추가

새 파일 `Take1Server/AgonesManager.cs` — SDK 연결 / Ready / 주기적 Health / Shutdown을 한 곳에 모읍니다:

```csharp
using Agones;

class AgonesManager
{
    private readonly AgonesSDK sdk = new AgonesSDK();
    private CancellationTokenSource healthCts;
    private readonly Action<string> log;

    public AgonesManager(Action<string> log)
    {
        this.log = log;
    }

    // 부팅 시 1회 호출: SDK 연결 + Ready 보고 + 헬스 루프 시작
    public async Task<bool> InitAsync()
    {
        bool connected = await sdk.ConnectAsync();
        if (!connected)
        {
            log("Agones: ConnectAsync 실패 (사이드카 없음?)");
            return false;
        }

        var ready = await sdk.ReadyAsync();
        if (!ready.Success)
        {
            log("Agones: ReadyAsync 실패");
            return false;
        }
        log("Agones: Ready 보고 완료. 매치 대기 중.");

        // 백그라운드 헬스 핑 (기본 2초마다)
        healthCts = new CancellationTokenSource();
        _ = HealthLoopAsync(healthCts.Token);

        // 할당(Allocated) 감지 콜백 (선택)
        sdk.WatchGameServer(gs =>
        {
            log($"Agones: 상태 변경 → {gs.Status.State}");
        });

        return true;
    }

    private async Task HealthLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await sdk.HealthAsync(); }
            catch (Exception ex) { log("Agones: Health 실패 " + ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(2), token).ContinueWith(_ => { });
        }
    }

    // 게임 종료 시 호출: 파드 정리 요청
    public async Task ShutdownAsync()
    {
        healthCts?.Cancel();
        var result = await sdk.ShutdownAsync();
        log("Agones: Shutdown 요청 (" + (result.Success ? "성공" : "실패") + ")");
    }
}
```

### 3-3. ChatServer.cs 수정

**(a) 필드 추가** — 클래스 상단:

```csharp
private AgonesManager agones;
```

**(b) Start() 시작 부분** — GameLift `InitSDK`/`ProcessReady` 주석 블록을 아래로 교체.
`listener.Start()` 직후, while 루프 들어가기 전에 Agones 초기화:

```csharp
listener.Start();
writeLog("Server is listening on port " + port);

// === Agones 연동 ===
agones = new AgonesManager(s => writeLog(s));
// 로컬 개발(사이드카 없음) 시에는 실패해도 계속 진행하도록 결과를 무시 가능.
agones.InitAsync().GetAwaiter().GetResult();

while (!stopping)
{
    ...
}
```

> `Start()`를 `async Task`로 바꿔서 `await agones.InitAsync()`를 쓰는 것이 더 깔끔합니다.
> 그 경우 `Program.cs`도 `await svr.Start();`로 변경하세요.

**(c) 게임 종료 지점** — `MsgCode.Play` 처리 중 승패가 갈려
`Environment.Exit(0)`를 호출하던 자리(GameLift `ProcessEnding` 주석 위치).

> **AI 앵커:** `ChatServer.cs`에서 아래 패턴을 찾을 것 —
> `writeLog(clients[loserKey].Name ...)` 다음에 나오는 주석
> `// GenericOutcome processEndingOutcome = GameLiftServerAPI.ProcessEnding();` 블록과
> 그 아래 `System.Threading.Thread.Sleep(2000);` + `System.Environment.Exit(0);`.
> 이 `Sleep` 바로 앞에 Shutdown 호출을 삽입한다. (승자 분기 안, else 블록)

```csharp
// (기존 ProcessEnding 주석 블록은 삭제)

// === Agones: 한 판 종료 → 파드 정리 요청 ===
// agones 가 null 일 수 있으므로(로컬/초기화 실패) 널 가드.
agones?.ShutdownAsync().GetAwaiter().GetResult();

System.Threading.Thread.Sleep(2000);
System.Environment.Exit(0);
```

> 주의: 이 분기는 `StartReceiving`의 백그라운드 `Task` 안에서 실행되므로 `await`를
> 직접 못 씁니다. 그래서 `.GetAwaiter().GetResult()`로 동기 대기합니다(종료 직전이라 무방).

**(d) (선택) 플레이어 검증** — `MsgCode.Ready` 처리부의 `AcceptPlayerSession` 주석은
Agones 기본(Stable) 기능에는 대응 항목이 없습니다. 플레이어 추적이 꼭 필요하면
Agones **Player Tracking (Alpha)** 의 `sdk.Alpha().PlayerConnectAsync(id)`를 쓰고,
아니면 그냥 생략하세요(가위바위보엔 불필요).

### 3-4. Program.cs (async 권장)

```csharp
var svr = new ChatServer();
await svr.Start();   // Start()를 async Task로 변경한 경우
```

---

## 4. 컨테이너 이미지

Agones는 게임 서버를 **컨테이너 이미지**로 받습니다. 멀티스테이지 Dockerfile 예시
(`Take1Server/Dockerfile`):

```dockerfile
# --- build ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -r linux-x64 --self-contained false -o /app

# --- runtime ---
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app ./
EXPOSE 7777
ENTRYPOINT ["dotnet", "Take1Server.dll"]
```

빌드 & 푸시:

```bash
docker build -t <레지스트리>/rsp-server:0.1 .
docker push <레지스트리>/rsp-server:0.1
```

---

## 5. 쿠버네티스 매니페스트

### 5-1. Agones 설치 (클러스터에 1회)

```bash
kubectl create namespace agones-system
helm repo add agones https://agones.dev/chart/stable
helm repo update
helm install agones agones/agones --namespace agones-system
```

### 5-2. GameServer (단일 테스트용) — `gameserver.yaml`

```yaml
apiVersion: agones.dev/v1
kind: GameServer
metadata:
  name: rsp-server
spec:
  ports:
    - name: default
      containerPort: 7777     # 컨테이너 내부 포트 (코드의 7777과 일치)
      protocol: TCP
  health:
    initialDelaySeconds: 5    # 부팅 후 첫 Health까지 유예
    periodSeconds: 5          # 이 주기 안에 HealthAsync()가 와야 함
    failureThreshold: 3
  template:
    spec:
      containers:
        - name: rsp-server
          image: <레지스트리>/rsp-server:0.1
```

> **중요:** 코드의 `HealthAsync()` 호출 주기(예: 2초)가 `periodSeconds`(5초)보다
> 짧아야 합니다. 안 그러면 Agones가 죽은 것으로 보고 파드를 재시작합니다.

배포 & 확인:

```bash
kubectl apply -f gameserver.yaml
kubectl get gameservers      # STATE가 Scheduled → Ready 로 바뀌면 성공
```

### 5-3. Fleet (운영용) — `fleet.yaml`

```yaml
apiVersion: agones.dev/v1
kind: Fleet
metadata:
  name: rsp-fleet
spec:
  replicas: 3                 # 항상 Ready 상태로 워밍업해 둘 서버 수
  template:
    spec:
      ports:
        - name: default
          containerPort: 7777
          protocol: TCP
      health:
        initialDelaySeconds: 5
        periodSeconds: 5
        failureThreshold: 3
      template:
        spec:
          containers:
            - name: rsp-server
              image: <레지스트리>/rsp-server:0.1
```

```bash
kubectl apply -f fleet.yaml
kubectl get fleet
```

### 5-4. 서버 할당 — `allocation.yaml`

매치메이커가 빈 서버 1개를 잡을 때:

```yaml
apiVersion: allocation.agones.dev/v1
kind: GameServerAllocation
metadata:
  generateName: rsp-alloc-
spec:
  selectors:
    - matchLabels:
        agones.dev/fleet: rsp-fleet
```

```bash
kubectl create -f allocation.yaml
# 결과의 status.gameServerName / address / ports 를 클라이언트에 전달
```

---

## 6. Health 주기 튜닝 (중요)

`HealthAsync()` 호출 주기는 Agones가 정해주는 게 아니라 **서버 코드의 루프 대기 시간**으로
결정됩니다. 두 개의 값이 짝을 이룹니다.

### 두 값의 역할

| 위치 | 값 | 의미 |
|---|---|---|
| 코드 (`AgonesManager.HealthLoopAsync`) | `Task.Delay(TimeSpan.FromSeconds(2))` | **내가 핑을 쏘는 간격** |
| YAML (`health.periodSeconds`) | `5` | 이 시간 안에 핑이 안 오면 1회 실패로 카운트 |
| YAML (`health.failureThreshold`) | `3` | 연속 N회 실패 시 Unhealthy → 파드 재시작 |
| YAML (`health.initialDelaySeconds`) | `5` | 부팅 후 첫 핑까지 봐주는 유예 |

위 예시(코드 2초 / period 5초 / threshold 3)면: 5초 안에 핑 1회를 기대하고,
연속 3번(약 15초) 비면 죽은 것으로 보고 재시작.

### 조정 방법

코드의 `Task.Delay` 숫자를 바꿉니다.

```csharp
await Task.Delay(TimeSpan.FromSeconds(2), token)...   // 2초마다
await Task.Delay(TimeSpan.FromMilliseconds(500), token)... // 0.5초마다
```

그리고 **반드시** YAML의 `periodSeconds`를 그보다 크게 맞춥니다.

### 황금 규칙: 코드 주기 < periodSeconds

> `periodSeconds`는 코드 핑 주기의 **2~3배**로 잡는 것을 권장.
> 네트워크 지연·일시 부하로 핑 한두 개가 늦어도 견디게 하기 위함.

| 감지 속도 | 코드 `Task.Delay` | YAML `periodSeconds` |
|---|---|---|
| 무난 (기본값) | 2초 | 5 |
| 느슨 | 5초 | 15 |
| 빠른 감지 | 1초 | 3 |

**안티패턴:** 코드 6초 + period 5초 → 정상 서버인데 핑이 늘 늦게 와서 무한 재시작.

> 즉 주기 조정 = ① 코드 `Task.Delay` 숫자 변경 + ② YAML `periodSeconds`를 그보다 크게,
> 이 **두 곳을 항상 함께** 손본다.

---

## 7. 로컬 개발 — 사이드카 없이 테스트

사이드카 없이 로컬에서 돌리면 `ConnectAsync()`가 실패합니다. 두 가지 방법:

**(a) 로컬 SDK 서버 실행** (권장 — 실제 흐름 검증)

```bash
# Agones SDK 서버를 로컬 단독 모드로 실행
sdk-server --local
# 이후 dotnet run 하면 localhost:9357 로 붙어 Ready/Health 가 콘솔에 찍힘
```

**(b) 코드에서 우회** — `InitAsync()` 실패 시 그냥 로깅하고 계속 진행하도록
이미 위 3-3(b)에서 결과를 무시하게 작성했으므로, 로컬에선 TCP 서버로만 동작합니다.

**환경 감지 팁** — Agones는 파드에 `AGONES_SDK_GRPC_PORT` 등 환경변수를 주입합니다.
이걸로 "클러스터 안인지"를 판단해 로컬에선 초기화를 아예 건너뛸 수 있습니다.

```csharp
bool inCluster = Environment.GetEnvironmentVariable("AGONES_SDK_GRPC_PORT") != null;
if (inCluster)
    agones.InitAsync().GetAwaiter().GetResult();
else
    writeLog("Agones: 로컬 모드 — SDK 초기화 생략");
```

---

## 8. 그레이스풀 셧다운 & 시그널 처리

게임이 정상 종료(`GameOver`)되는 경우 외에도, 쿠버네티스가 파드를 내릴 때
**SIGTERM**을 보냅니다(노드 정리, 스케일다운, 배포 교체 등). 이때도 Agones에
정상적으로 알리고 빠져나가는 게 좋습니다.

`AgonesManager`에 추가:

```csharp
// Program.cs 또는 Start() 초기에 1회 등록
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    try { agones?.ShutdownAsync().GetAwaiter().GetResult(); } catch { }
};
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;            // 즉시 죽지 말고 정리할 시간 확보
    agones?.ShutdownAsync().GetAwaiter().GetResult();
    Environment.Exit(0);
};
```

> 가위바위보처럼 한 판이면 끝나는 단명 서버는 이게 필수는 아니지만, 배포 교체 중
> 진행 중이던 매치를 깔끔히 정리하려면 넣어두는 게 안전합니다.

### Allocated 상태를 쓰는 경우 (선택)

기본 플로우는 "Ready → 바로 접속"이지만, 매치메이커를 통해 할당받는 구조라면
서버가 `WatchGameServer` 콜백에서 `Allocated` 전이를 감지한 뒤에야 클라이언트 접속을
받도록 게이팅할 수 있습니다. 현재 가위바위보 서버는 접속 즉시 방에 넣으므로
이 단계는 생략 가능하며, 도입 시 `admissionLock` 근처에서 "Allocated 여부"를
추가 조건으로 검사하면 됩니다.

---

## 9. 트러블슈팅

| 증상 | 원인 | 해결 |
|---|---|---|
| `ConnectAsync()`가 로컬에서 실패 | 사이드카/SDK 서버 없음 | `sdk-server --local` 실행하거나 §7(b) 환경변수로 우회 |
| GameServer가 계속 재시작 | 코드 Health 주기 ≥ `periodSeconds` | §6 황금 규칙대로 코드 주기를 더 짧게 |
| STATE가 `Scheduled`에서 안 넘어감 | 이미지 pull 실패 / `ReadyAsync()` 미호출 | `kubectl describe gs <name>` 로 이벤트 확인, Ready 호출 여부 점검 |
| STATE `Unhealthy` 반복 | `initialDelaySeconds` 너무 짧아 첫 핑 전에 컷 | `initialDelaySeconds` 늘리기 (부팅 시간 고려) |
| 클라이언트가 접속 못 함 | 컨테이너 포트 != 코드 포트(7777) | YAML `containerPort`와 코드 `port` 일치 확인 |
| 외부에서 IP:Port 모름 | Agones가 호스트 포트를 동적 할당 | Allocation 결과의 `status.ports[].port`와 `status.address` 사용 |
| Shutdown 후에도 파드 남음 | `ShutdownAsync()` 미await / 예외 | 종료 직전 `.GetAwaiter().GetResult()` 확인, 로그 점검 |

유용한 명령:

```bash
kubectl get gs                       # GameServer 상태 일람
kubectl describe gs <name>           # 이벤트/에러 상세
kubectl logs <pod> -c rsp-server     # 게임 서버 컨테이너 로그
kubectl logs <pod> -c agones-gameserver-sidecar   # 사이드카 로그
```

---

## 10. 적용 체크리스트

- [ ] `dotnet add package AgonesSDK`
- [ ] `Take1Server.csproj`에서 GameLift `<Reference>`/`AWSSDK.GameLift` 제거 (CloudWatch는 유지)
- [ ] `AgonesManager.cs` 추가
- [ ] `ChatServer.Start()` 에 `InitAsync()` 연결 (GameLift InitSDK/ProcessReady 자리)
- [ ] 게임 종료부에 `ShutdownAsync()` 연결 (GameLift ProcessEnding 자리, `Exit(0)` 직전)
- [ ] SIGTERM/ProcessExit 핸들러 등록 (§8)
- [ ] 환경변수 기반 로컬/클러스터 분기 (§7)
- [ ] `Dockerfile` 작성 → 이미지 빌드 & 레지스트리 푸시
- [ ] 클러스터에 Agones 설치 (helm)
- [ ] `gameserver.yaml` 로 단일 서버 검증 (STATE: Ready)
- [ ] `fleet.yaml` 로 N개 워밍업
- [ ] `allocation.yaml` 로 할당 → 클라이언트 접속 테스트
- [ ] **코드 Health 주기 < YAML `periodSeconds`** 인지 확인 (§6)

---

## 11. AI 에이전트 구현 프롬프트 (복붙용)

아래를 그대로 AI 코딩 에이전트에 전달하면 이 가이드 기반으로 구현을 수행합니다.

```
RockScissorsPaper/Take1Server (.NET 8 TCP 게임서버)를 Agones로 연동해줘.
AGONES_GUIDE.md를 단일 진실 소스로 삼고, 거기 명시된 소스맵·앵커·불변식을 지켜.

수행 작업:
1. Take1Server.csproj: `AgonesSDK` 패키지 추가, GameLift Reference/AWSSDK.GameLift 제거
   (AWSSDK.CloudWatchLogs는 유지).
2. AgonesManager.cs 신규 생성 — 가이드 §3-2 코드 그대로
   (Connect→Ready→Health 루프→Watch, ShutdownAsync 포함).
3. ChatServer.cs 수정:
   - 필드 `private AgonesManager agones;` 추가
   - `listener.Start()` 직후 환경변수(AGONES_SDK_GRPC_PORT) 분기로 InitAsync 호출 (§7)
   - 승패 판정 후 `Environment.Exit(0)` 직전에 `agones?.ShutdownAsync()...` 삽입 (§3-3c 앵커)
   - 주석 처리된 GameLift 코드 블록은 삭제
4. Program.cs: async 진입점으로 변경 (필요 시).
5. SIGTERM/ProcessExit 핸들러 등록 (§8).
6. Dockerfile, gameserver.yaml, fleet.yaml, allocation.yaml 생성 (§4, §5).

불변식(절대 위반 금지):
- 게임 판정 로직 `(card0 % 3)+1==card1`, MsgCode 프로토콜, 2인 매치 규칙 변경 금지
- 로컬(사이드카 없음)에서도 서버는 죽지 않고 TCP로 동작해야 함
- 코드 Health 주기 < YAML periodSeconds 유지

완료 후 `dotnet build`로 컴파일 확인하고, 변경 파일 목록과 diff 요약을 보고해.
```

---

## 12. 참고 링크

- Agones 공식 문서: https://agones.dev/site/docs/
- C# SDK 가이드: https://agones.dev/site/docs/guides/client-sdks/csharp/
- GameServer 라이프사이클: https://agones.dev/site/docs/integration-patterns/
- Fleet / Allocation: https://agones.dev/site/docs/reference/fleet/
- Health checking: https://agones.dev/site/docs/guides/health-checking/
