# RockScissorsPaper — EKS(Agones) 배포 런북

`Take1Server` 게임서버 이미지를 EKS `prod` 클러스터의 Agones로 배포하는 절차입니다.

## 사전 정보
- 클러스터: `prod` (ap-northeast-2), Agones 설치 완료
- 이미지: `000000000000.dkr.ecr.ap-northeast-2.amazonaws.com/take1server`
- 매니페스트: `Take1Server/k8s/` (`gameserver.yaml`, `fleet.yaml`, `allocation.yaml`)
- 모든 kubectl 명령은 리포 루트(`RockScissorsPaper/`)에서 실행

---

## 0. kubectl 컨텍스트 연결 (최초 1회)
```bash
aws eks update-kubeconfig --name prod --region ap-northeast-2
kubectl config current-context      # eunhak@prod.ap-northeast-2.eksctl.io 확인
```

## 1. (선택) 단일 GameServer로 이미지 검증
운영 배포 전에 이미지가 `Ready`까지 가는지 확인할 때만 사용.
```bash
kubectl apply -f Take1Server/k8s/gameserver.yaml
kubectl get gs rsp-server -o wide          # STATE가 Scheduled -> Ready 면 성공
kubectl delete -f Take1Server/k8s/gameserver.yaml   # 검증 후 정리
```

## 2. Fleet 배포 (운영)
```bash
kubectl apply -f Take1Server/k8s/fleet.yaml
kubectl get fleet rsp-fleet                # READY 가 replicas(3)까지 차면 완료
kubectl get gs -o wide                     # 서버별 주소/포트 확인
```

## 3. 서버 할당 (매치메이커가 빈 서버 1개 확보)
```bash
kubectl create -f Take1Server/k8s/allocation.yaml -o yaml | grep -A6 "status:"
# status.address + status.ports[0].port 를 클라이언트에 전달해 접속
```
할당된 서버는 `Allocated` 로 바뀌고, 한 판 종료 시 프로세스가 내려가면
Fleet이 자동으로 새 Ready 서버를 채웁니다.

---

## 새 이미지로 롤아웃
CodeBuild가 새 이미지를 push하면(커밋 태그 사용), 매니페스트의 태그를 바꿔 다시 apply.
```bash
# fleet.yaml 의 image 태그(:d7ad8d5)를 새 커밋 태그로 수정 후
kubectl apply -f Take1Server/k8s/fleet.yaml
```
> `:latest`는 롤아웃 추적/롤백이 어려우니 커밋 태그 사용을 권장.

## 상태 확인 / 디버깅
```bash
kubectl get fleet rsp-fleet
kubectl get gs -o wide
kubectl describe gs <name>                          # 이벤트/에러
kubectl logs <pod> -c rsp-server                    # 게임 로그
kubectl logs <pod> -c agones-gameserver-sidecar     # Agones 사이드카 로그
```

## GameServer 수동 삭제 / 할당 해제 (디버깅용)
테스트 중 `Allocated` 서버가 쌓였거나 특정 GameServer를 강제로 회수하고 싶을 때 사용.
`Allocated` 상태여도 그냥 삭제할 수 있고, Fleet 소속이면 삭제 즉시 새 GameServer가
생성되어 `replicas`(현재 3)를 자동으로 맞춥니다.
```bash
# 1) 현재 GameServer와 상태 확인 (STATE=Allocated 인 것 파악)
kubectl get gs -o wide

# 2) 특정 GameServer 삭제 -> Fleet이 Ready로 재충전
kubectl delete gs <gameserver-name>
kubectl delete gs <name1> <name2> ...      # 여러 개 동시 삭제

# 3) rsp-fleet의 Allocated 서버 일괄 삭제 (Agones 버전에 따라 field-selector 미지원일 수 있음)
kubectl delete gs -l agones.dev/fleet=rsp-fleet --field-selector status.state=Allocated
```
> 참고
> - 운영에서는 서버가 게임 종료 시 SDK `ShutDown()`을 호출해 스스로 내려가는 것이 정석입니다. 수동 삭제는 디버깅/강제 회수용입니다.
> - `GameServerAllocation` 리소스를 지워도(`kubectl delete gameserverallocation ...`) 이미 `Allocated`된 GameServer는 `Ready`로 돌아오지 않습니다. 상태 해제는 위처럼 GameServer 자체를 삭제하거나 서버가 `ShutDown()`/`Ready()`를 호출해야 합니다.

## 정리(삭제)
```bash
kubectl delete -f Take1Server/k8s/fleet.yaml        # rsp-fleet + 하위 GameServer 전부 삭제
kubectl delete -f Take1Server/k8s/gameserver.yaml --ignore-not-found
```

---

## 참고: 외부 접속 (게임 포트)
Agones는 노드 호스트포트를 기본 7000–8000 범위에서 동적 할당합니다.
외부 클라이언트 접속이 필요하면 노드 보안그룹 인바운드에 해당 포트 범위(TCP)를
허용해야 합니다. 접속 주소는 GameServer의 `status.address:status.ports[].port`.

### 적용된 보안그룹 규칙 (이력)
- 대상 SG: `sg-xxxxxxxx` (`eks-cluster-sg-<cluster>-xxxx`) — EKS 워커노드에 부착된 클러스터 SG
- 추가 규칙: 인바운드 **TCP 7000–8000**, 소스 **`<office-ip>/32`** — 2026-07-01
- 서버가 TCP인데 기존엔 UDP만 열려 있어 접속 불가였음 → TCP 규칙 추가로 해결
- 실제 SG ID / 사무실 IP 등 구체값은 비공개 일지(`WORKLOG.md`, gitignore) 참조

추가 명령:
```bash
aws ec2 authorize-security-group-ingress \
  --region ap-northeast-2 \
  --group-id <sg-id> \
  --ip-permissions 'IpProtocol=tcp,FromPort=7000,ToPort=8000,IpRanges=[{CidrIp=<office-ip>/32,Description="Agones game TCP - office"}]'
```

회수(제거) 명령:
```bash
aws ec2 revoke-security-group-ingress \
  --region ap-northeast-2 \
  --group-id <sg-id> \
  --ip-permissions 'IpProtocol=tcp,FromPort=7000,ToPort=8000,IpRanges=[{CidrIp=<office-ip>/32}]'
```

현재 규칙 확인:
```bash
aws ec2 describe-security-groups --region ap-northeast-2 \
  --group-ids <sg-id> \
  --query 'SecurityGroups[0].IpPermissions'
```
> 새 사무실/클라이언트 IP가 생기면 같은 형식으로 `/32`를 추가하세요. 프로덕션 SG이므로 `0.0.0.0/0` 개방은 지양.
