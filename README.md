# RimWorld Korean IME 실험

RimWorld 1.7 Waylith의 한국어 IME 입력 경로를 조사한 기록과 다음 Harmony 패치 실험의 시작점입니다.

## 현재 상태

- 기존 IME 계측, preedit 렌더링, 지역 검색, ResetToOff 실험 코드는 제거했습니다.
- 실행 코드에는 `Source/Main.cs`의 `HarmonyPatchEntryPointMock`만 있습니다.
- Mock은 이 어셈블리에 `Harmony.PatchAll`을 호출하지만 현재 `[HarmonyPatch]` 대상이 없어 게임 동작을 바꾸지 않습니다.
- 기존 실험 결과와 절차는 [조사 기록](docs/agent/RimWorld_1.7_Waylith_Korean_IME_Investigation.md)에 보존했습니다.

## 실험 요약

| 단계 | 결과 |
| --- | --- |
| 1. Unity/IMGUI 계측 | Harmony 바인딩은 확인. 실제 조합 콜백은 관찰되지 않음. |
| 2. GLFW/Windows 메시지 | 게임 로그에서 `ㅇ → 아 → 안` 조합 문자열을 확인. |
| 3. 화면 preedit | 단일 행 조합 오버레이가 게임에서 표시됨. 검색은 확정 시점에 갱신됨. |
| 4. 지역 검색 preview | 자동 검증은 통과했으나 게임에서 실패하여 중단. |
| 5. 공통 TextField 반환값 | 자동 검증과 Harmony 바인딩은 통과. 실제 게임 입력 결과는 미확인. |
| 보조 진단 | IMM 상태, ResetToOff 버튼/액션, `]` 단축키는 실험용이며 현재 실행 경로에서 제거됨. |

## 빌드

`.NET 10 SDK`와 RimWorld 1.7 설치본이 필요합니다. 기본 경로가 다르면 `RIMWORLD_DIR` 또는 `-p:RimWorldDir="게임 루트"`를 지정합니다.

```powershell
dotnet build package.csproj
```

Harmony는 게임의 `brrainz.harmony` 모드가 제공합니다. 프로젝트는 컴파일 시 `Lib.Harmony.Ref`를 사용합니다.
