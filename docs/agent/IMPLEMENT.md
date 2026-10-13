# RimWorld Korean IME Harmony 패치

RimWorld 1.7 Waylith의 Windows 한국어 IME 브리지입니다. 구현 근거와 실게임 확인 목록은 [조사 문서](docs/agent/IME_HARMONY_INVESTIGATION.md)에 있습니다. [구현 및 검증 기록](docs/agent/IME_HARMONY_IMPLEMENTATION.md)도 함께 확인하세요.

- 실제 GLFW HWND에 같은 스레드의 `SetWindowSubclass`를 설치합니다. 호출되지 않는 `ResetToOff`에 의존하지 않습니다.
- 편집 가능한 공통 TextField/TextArea 밖에서는 빌린 IME 컨텍스트를 분리하고, 필드 안에서는 복원합니다. 포커스 이동·Alt+Tab 때 미확정 조합은 취소합니다. 컨텍스트를 파괴하지 않습니다.
- 조합 문자열·내부 커서를 기존 GUI 렌더러에만 전달하고 밑줄과 후보창 좌표를 갱신합니다. 반환 문자열과 TextEditor에는 확정 문자만 들어갑니다.
- START/COMPOSITION 메시지 처리를 인수합니다. `GCS_RESULTSTR`를 순서대로 큐에 넣어 이전 소유 필드의 기존 GUI 편집 경로로 전달하며, 해당 메시지를 기본 처리에 넘겨 중복 WM_CHAR를 만들지 않습니다. 일반 영문 WM_CHAR 경로는 유지합니다.
- IME 조합 중 Windows 키 메시지에서 확인한 제어키만 해당 프레임의 GUI keyDown에서 차단합니다. keyUp과 다른 단축키는 유지합니다.
- 읽기 전용·비밀번호 필드는 제외합니다. 길이 제한·정규식·숫자 검증은 기존 확정 입력 경로가 담당합니다. TMP/custom editor는 별도 어댑터가 필요합니다.
- 훅 설치 또는 조합 조회/렌더링 실패 시 브리지를 해제하고 기존 입력으로 복귀합니다. 입력 문자열은 로그에 기록하지 않습니다.

## 빌드와 확인

.NET 10 SDK와 RimWorld 1.7 설치본이 필요합니다. 다른 설치 위치는 `RIMWORLD_DIR` 또는 `-p:RimWorldDir="게임 루트"`로 지정합니다. Harmony는 게임의 `brrainz.harmony` 모드가 제공합니다.

```powershell
dotnet build package.csproj
dotnet run --project tests/ImePatch.Smoke.csproj -p:NuGetAudit=false
```

산출물은 `1.7/Assemblies/Template.dll`입니다. 기존 파일명과 package ID를 유지했습니다. 시작 로그의 `IME Harmony patches installed (15 hooks)`와 `Windows IME bridge attached`를 확인합니다. 이전 IMEProbe 실험 모드를 함께 활성화하지 마세요.

Smoke는 실제 게임 DLL에 Harmony를 적용하고 대역 렌더러를 사용합니다. 실제 Windows 한국어 입력, 확정 메시지 중복 여부, 후보창/DPI, 게임 내 W/A/S/D 및 모달 동작은 자동 확인하지 않습니다. 네이티브 테스트 프로세스에서 IMM 컨텍스트를 만들 수 없으면 해당 검사는 명시적으로 SKIP합니다.

이전 실험 이력은 [기존 조사 기록](docs/agent/RimWorld_1.7_Waylith_Korean_IME_Investigation.md)에 보존했습니다.
