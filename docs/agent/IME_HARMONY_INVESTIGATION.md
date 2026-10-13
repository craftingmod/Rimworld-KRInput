# RimWorld 1.7 Waylith: Windows 한글 IME와 Harmony 패치 조사

조사일: 2026-10-13. 대상은 현재 디컴파일 프로젝트와 `G:\SteamLibrary\steamapps\common\RimWorld`의 실제 DLL이다. 게임 실행 및 실제 IME 계측은 하지 않았다. 아래의 코드 확인과 런타임 가설을 구분한다.

## 결론

`Waylith.PlatformSpecific.WindowsIme`부터 시작하는 것이 타당하다. 그러나 이 클래스의 `ResetToOff()`를 패치하는 것만으로 해결되지는 않는다. 현재 이 함수는 IME를 영문 상태로 한 번 닫을 뿐이며, 조합 문자열 수신, 게임 TextField 포커스 연동, OS 조합창 숨김, 커서 좌표 전달은 구현되어 있지 않다.

권장 구현은 **Harmony 모드 안의 Windows IME 브리지 + 공통 UnityStubs GUI 패치**이다. C#에서 Windows API를 P/Invoke할 수 있으므로 별도의 C++ DLL은 현재 증거로는 필요하지 않다. Harmony 자체가 네이티브 GLFW WindowProc에 Prefix를 붙이는 것은 아니며, 관리 코드 패치로 Windows 창 서브클래스의 설치와 수명을 연결한다.

IME 엔진은 한글 조합을 위해 유지한다. 숨길 대상은 OS의 기본 조합 표시창이며, 게임은 조합 중 문자열을 입력창 안에 직접 그린다. 입력창 밖에서는 창의 IME 연결을 일시 해제하는 방식으로 게임 키를 정상 처리하고, 입력창에 들어오면 기존 입력 컨텍스트를 복원한다. Windows/IME 버전별 동작은 실게임 검증이 필요하다.

## 1. WindowsIme.cs에서 확인한 사실

파일: `Waylith.PlatformSpecific/WindowsIme.cs`.

- `ResetToOff()`는 Windows 여부를 확인하고 `GetActiveWindow → ImmGetContext → ImmGetOpenStatus → ImmSetOpenStatus(false) → ImmReleaseContext`만 수행한다.
- `ImmReleaseContext`를 finally에서 호출하는 자원 처리 방식은 유지해야 한다.
- `GetActiveWindow()`는 호출 스레드의 활성 창에 의존한다. 브리지에서는 GLFW 게임 창에서 얻은 확실한 HWND를 사용한다.
- 전체 현재 프로젝트에서 `ResetToOff()` 호출자는 발견되지 않았다. 설치 디렉터리 DLL의 이름 검색에서도 본체 외 참조는 기존 `Mods/IMEProbe`에 있었다. 이것은 동적 reflection 호출 부재까지 증명하지는 않는다.
- 따라서 이 함수를 초기화 진입점으로만 삼으면 호출되지 않아 브리지가 설치되지 않을 수 있다.

실제 HWND를 얻는 API는 설치된 `Brutal.Glfw.dll`에 존재한다:

```csharp
// 기존 라이브러리의 실제 공개 API. 이 예는 설치 위치를 설명한다.
nint hwnd = Brutal.GlfwApi.Glfw.PInvoke.GetWin32Window(app.Window);
```

`GlfwWindow.NativeHandle`은 GLFWwindow 포인터다. HWND가 아니므로 `ImmGetContext`에 직접 넘기면 안 된다.

## 2. KeyCode가 사라지는 원인 경로

GLFW Windows 소스는 `WM_KEYDOWN/WM_KEYUP` 처리에서 `wParam == VK_PROCESSKEY`이면 키 콜백 호출을 건너뛴다. 이 값은 IME가 키를 처리했음을 나타낸다. [GLFW Windows 구현](https://github.com/glfw/glfw/blob/master/src/win32_window.c)

**게임에 포함된 실제 glfw3.dll에서도 해당 분기를 확인했다.** `.research/glfw3.asm`의 가상주소 `0x1800184D4`에 `cmp r15, 0xe5`, 다음 명령에 `je 0x180018BFF`가 있다. 그 분기는 키 전달 코드를 건너뛰어 기본 창 처리 경로로 이동한다. 주소는 이번 DLL에만 해당하며 패치 주소로 사용하지 않는다.

관리 코드에는 서로 다른 두 경로가 있다:

```text
Windows 키 메시지 → glfw3.dll → Brutal.Glfw.GlfwWindow.OnKey
  ├─ Waylith.Input.GlfwInputSource.OnKey → Waylith.Input.InputState
  └─ RimWorldWaylith.GlfwInputBridge.OnKey → UnityEngine.InputSystem
       → Keyboard/KeyControl → UnityEngine.Input / RawKeys
       → ImguiDispatch.DispatchKeyboard → Event.current.keyCode
```

- `GlfwInputSource.OnKey`는 Press/Release만 반영한다.
- **RimWorld의 `GlfwInputBridge.OnKey`는 Press/Release/Repeat를 Unity 입력 상태에 별도로 반영한다.** 따라서 `InputState.OnKey`에만 W/A/S/D를 넣는 패치는 게임의 Unity `KeyCode` 경로를 복구하지 못한다.
- `UnityEngine.Input.GetKey/GetKeyDown/GetKeyUp`은 TextField가 소비하는 키를 의도적으로 차단한다. 입력창 안에서 이동 키가 동작하지 않는 것은 별개의 정상적인 포커스 정책이다. 해당 정책을 전역 제거하면 한글 입력 중 카메라까지 이동할 수 있다.
- `GlfwInputSource.OnKey` Prefix만으로는 GLFW가 전달하지 않은 키를 복원할 수 없다.

런타임에서 `WM_KEYDOWN(VK_PROCESSKEY) → OnKey 미발생`을 함께 기록하면 사용자 증상의 원인을 최종 확인할 수 있다. 현재는 실제 DLL 분기와 관찰된 증상이 부합하는 단계다.

## 3. 한 글자가 확정될 때까지 TextField에 보이지 않는 이유

실제 설치 DLL을 디컴파일하여 확인한 경로:

```text
Windows 확정 문자 → GLFW OnChar
  → RimWorldWaylith.GlfwInputBridge.OnChar
  → InputSystem.QueueTextInput(char) → Keyboard.onTextInput
  → ImguiDispatch._typedChars
  → RunKeyPass(KeyCode.None, character)
  → GUI.TextFieldImpl → ApplyTextKey → 확정 문자열에 삽입
```

확인된 미구현 부분:

| 위치 | 실제 구현 | 결과 |
| --- | --- | --- |
| Waylith GlfwInputSource / RimWorld GlfwInputBridge | 키·확정 문자 콜백만 구독 | 조합 갱신은 전달되지 않음 |
| UnityEngine.Input.compositionString | 항상 `string.Empty` | 모드가 Unity IME API를 읽어도 조합 문자열 없음 |
| Input.compositionCursorPos / imeCompositionMode | Stub 경고만 출력 | 좌표·활성화 설정 효과 없음 |
| Keyboard.OnIMECompositionChanged | Stub 경고만 출력 | 조합 상태 전달 없음 |
| ImguiDispatch | onTextInput만 구독 | 확정 char만 GUI 이벤트로 전달 |
| GUI.DrawTextFieldChrome / DrawTextAreaContent | 확정 text만 렌더링 | `ㅇ → 아 → 안`이 게임 안에 보이지 않음 |

`안ㄴ`까지 입력해야 이전 `안`이 보이는 것은 **확정 입력만 처리되고 조합 표시가 빠진 구조**와 일치한다. 입력 지연이나 폰트 문제를 먼저 고치는 접근으로 해결될 구조가 아니다.

## 4. 권장 패치 구성

### A. 모드 내부 Windows IME 브리지

엔진 클래스 자체를 재컴파일하지 않고, `WindowsIme.cs`의 Windows API 접근을 출발점으로 모드에 브리지를 둔다.

1. 게임 창 생성 이후, 창을 소유한 스레드에서 HWND를 얻는다.
2. `SetWindowSubclass`로 해당 창의 메시지만 처리한다. 정적 delegate를 보관하고 `WM_NCDESTROY`/종료 시 해제한다. 처리하지 않은 메시지는 `DefSubclassProc`로 넘긴다.
3. `WM_IME_STARTCOMPOSITION`, `WM_IME_COMPOSITION`, `WM_IME_ENDCOMPOSITION`을 받는다.
4. `ImmGetCompositionStringW`의 `GCS_COMPSTR`, `GCS_CURSORPOS`로 조합 문자열과 조합 내부 커서를 읽어 보관한다. 조합이 빈 문자열인 갱신·취소도 처리한다.
5. 게임에서 조합 표시를 담당할 때 `WM_IME_SETCONTEXT`의 `ISC_SHOWUICOMPOSITIONWINDOW`만 제거한다. 후보창 등 나머지 UI 비트를 전부 지우지 않는다. 필요하다면 START/COMPOSITION 기본 UI 처리도 직접 인수하되, 아래 확정 입력 계약을 함께 구현한다.
6. 활성 TextField와 캐럿 위치를 전달받아 한자 후보창 위치를 `ImmSetCandidateWindow`로 갱신한다.

창 서브클래스는 같은 스레드에서 설치해야 한다. [SetWindowSubclass](https://learn.microsoft.com/en-us/windows/win32/api/commctrl/nf-commctrl-setwindowsubclass)

조합창 UI 비트 제거는 Microsoft가 안내하는 방식이다. [WM_IME_SETCONTEXT](https://learn.microsoft.com/en-us/windows/win32/intl/wm-ime-setcontext)

문자열 조회 API의 길이는 UTF-16 문자 수가 아니라 **바이트 수**이며 음수 오류 반환도 처리해야 한다. 읽은 컨텍스트는 finally에서 반환한다. [ImmGetCompositionStringW](https://learn.microsoft.com/en-us/windows/win32/api/imm/nf-imm-immgetcompositionstringw)

### B. TextField 밖에서는 IME 연결 해제, 안에서는 복원

`TextFocus.CapturesText`를 출발점으로 사용하되, 활성 컨트롤이 실제 편집 가능한 TextField인지도 기록한다. `keyboardControl != 0`만으로는 다른 키보드 컨트롤과 구분하기 어렵다. 읽기 전용 TextArea도 제외한다.

- 입력창 밖: `ImmAssociateContext(hwnd, 0)`으로 이전 HIMC를 받아 보관하고 창에서 분리한다. 매 프레임 반복하지 않고 상태가 바뀔 때만 처리한다.
- 입력창 안: 보관한 HIMC를 재연결한다. 기존 한/영·변환 상태가 유지되는지 확인하고, 필요하면 스냅샷으로 복원한다.
- 기존 HIMC는 빌린 자원이다. `ImmDestroyContext`로 파괴하면 안 된다.
- 포커스 전환 시 기존 조합을 확정 또는 취소하는 정책을 명시한다. 확정 문자는 **전환 전 소유 컨트롤**에 전달해야 한다.
- Alt+Tab, 모달 닫기, 입력창 소멸, 창 재생성 시 연결·조합·소유자 상태를 정리한다.

이 방식은 입력창 밖에서 IME가 키를 조합용으로 가져가는 상황 자체를 줄인다. [ImmAssociateContext](https://learn.microsoft.com/en-us/windows/win32/api/imm/nf-imm-immassociatecontext), [Windows Forms IME 컨텍스트 처리](https://source.dot.net/system.windows.forms/System/Windows/Forms/Control.Ime.cs.html)

보조 대안은 서브클래스에서 `VK_PROCESSKEY`의 lParam 스캔코드를 바탕으로 키를 복원하는 것이다. 양쪽 입력 경로와 반복/해제/중복 억제를 함께 처리해야 하며, 입력창 밖에서도 IME가 조합을 누적하지 않도록 관리해야 한다. 이는 기본안보다 범위가 커진다.

**주의:** WndProc에서 `ImmGetVirtualKey()`만 호출하면 원래 키를 항상 얻는다고 가정할 수 없다. GLFW는 메시지 루프에서 TranslateMessage 후 DispatchMessage를 호출하며, Microsoft 문서는 원래 VK 조회를 TranslateMessage 이전으로 제한한다. [ImmGetVirtualKey](https://learn.microsoft.com/en-us/windows/win32/api/imm/nf-imm-immgetvirtualkey)

### C. 공통 TextField 렌더링에 조합 문자열 표시

패치 중심은 `UnityEngine.GUI.TextFieldImpl`와 `DrawTextFieldChrome`이다. `TextEditor.Insert`만 패치하는 것은 충분하지 않다. 실제 편집은 GUI의 `_caretIndex`, `_selectAnchor`, `ApplyTextKey`가 담당하고 TextEditor에 동기화한다.

렌더링 시만 표시 문자열을 만든다:

```text
저장/반환용 확정 문자열 = 앞부분 + 선택 영역 + 뒷부분
렌더링용 표시 문자열   = 앞부분 + 조합 문자열 + 뒷부분
```

- 조합 시작 시 컨트롤 ID, 선택 영역, 삽입 위치를 잡고 그 컨트롤에만 표시한다.
- `DrawTextFieldChrome` Prefix에서 표시 text와 표시용 caret/anchor를 전달하고 Finalizer에서 원래 편집 상태를 복원하는 방식이 후보이다.
- 캐럿은 조합 시작 위치 + `GCS_CURSORPOS`로 표시한다. 조합 범위는 밑줄 등으로 표시한다.
- 기존 폰트, clipping, 가로 스크롤, alignment를 사용한다. 다중 행은 기존 `DrawTextAreaContent`의 줄바꿈·스크롤 계산도 따라야 한다.
- `TextFieldImpl` 반환값과 `TextEditor.text`에는 미확정 조합을 섞지 않는다. 조합 문자열이 저장·검색·검증 코드에 확정 입력으로 새어 나가면 다음 프레임에 중복 삽입될 수 있다.
- `maxLength`/정규식/숫자 필드/비밀번호 필드의 계약은 별도로 존중한다. 조합 미리보기와 확정 후 검증을 구분한다.
- 조합 중 Backspace/Enter/Escape/방향키는 IME가 소비한 이벤트인지 구분한다. `ImguiDispatch`의 ControlKeys가 다시 처리하여 앞의 확정 글자를 지우거나 모달을 닫지 않게 한다. 후보 선택·조합 종료와 정상 단축키를 전부 막는 일괄 차단은 피한다.

커서 좌표는 현재 backend의 `GuiToScreenPoint`로 GUI 변환·클립 오프셋을 반영한 뒤 framebuffer/window 비율에 맞게 HWND client 좌표로 변환한다. `ImmSetCandidateWindow`를 사용하며 UI 배율·DPI·스크롤을 검증한다. [후보창 위치 API](https://learn.microsoft.com/en-us/windows/win32/api/imm/nf-imm-immsetcandidatewindow)

### D. 확정 문자열은 정확히 한 번 전달

가능한 방식은 두 가지이며 혼합하면 안 된다:

1. 조합 데이터만 관찰하고 기본 확정 문자 경로를 유지: 기존 OnChar → QueueTextInput을 그대로 쓰고 `GCS_RESULTSTR`를 다시 삽입하지 않는다. 기본 조합창을 숨긴 상태에서도 확정 경로가 유지되는지 확인해야 한다.
2. START/COMPOSITION 처리를 직접 인수: `GCS_RESULTSTR`를 읽어 모드의 확정 이벤트로 전달하고, 해당 IME 확정이 기본 WM_IME_CHAR/WM_CHAR/OnChar 경로로 중복 전달되지 않도록 메시지 단위로 관리한다. 단순히 문자열이 같다는 이유로 문자를 버리는 방식은 `ㅋㅋ`, 연속 동일 글자를 잃을 수 있다.

한 메시지에서 RESULTSTR와 새 COMPSTR가 함께 올 수 있다. 결과를 기존 컨트롤에 먼저 확정하고 새 조합을 이어가는 순서를 보존한다. 프레임 마지막 조합 상태만 저장하는 방식으로 확정 이벤트까지 덮어쓰면 안 된다.

## 5. Harmony 진입점과 구현 순서

| 순서 | 지점 | 역할 |
| --- | --- | --- |
| 1 | GlfwInputBridge 생성 이후 또는 모드에서 PrePollEvents에 연결 | 이미 생성된 HWND에 브리지 설치; 초기화 시점이 지난 경우도 지원 |
| 2 | Bootstrap.OnPrePollEvents Postfix 또는 그 이후의 모드 PrePoll 콜백 | TextField 포커스에 따라 IME 컨텍스트 전환 |
| 3 | Windows 서브클래스 | 조합 수신, 기본 조합창 숨김, 결과/취소 이벤트 보관 |
| 4 | GUI.TextFieldImpl / DrawTextFieldChrome | 실제 편집 필드 소유자·위치 등록, 조합 표시 |
| 5 | ImguiDispatch.DispatchKeyboard / ApplyTextKey의 필요한 부분 | 조합 중 제어키의 이중 처리 방지 |
| 6 | Input.compositionString getter, compositionCursorPos / imeCompositionMode setter | Unity 호환 IME API를 모드 상태로 연결 |

`PrePollEvents → InputState.BeginFrame → GLFW PollEvents` 순서이며 Bootstrap의 PrePoll 콜백에서 `InputSystem.BeginFrame`도 실행된다. **프레임 초기화 전에 합성 키를 주입하면 눌림 플래그가 바로 지워질 수 있다.** 포커스 정책과 훅 설치는 PrePoll에, 키 전달은 PollEvents 메시지 처리 중 또는 초기화 이후에 수행한다.

Harmony로 기존 타입에 필드/이벤트를 추가할 수는 없으므로 브리지 상태는 모드가 보관한다. private 메서드는 AccessTools로 찾고 실제 시그니처를 검사한다. 필요한 transpiler는 호출 패턴이 정확히 한 번 일치할 때만 설치하며 실패 시 기존 입력 동작으로 복귀한다.

`ResetToOff` Prefix를 붙여 설치하는 것만으로 끝내거나, 매 프레임 ResetToOff를 부르는 방식은 사용하지 않는다. 기존 ResetToOff 호출과 충돌 시 진단용 영문 전환 기능을 어떤 상태에서 허용할지만 명시적으로 정한다.

## 6. 기존 IMEProbe와 범위

설치 폴더에 `Mods/IMEProbe` 소스가 있었다. 수정하거나 실행하지 않았다.

- `Main.cs`의 시작 코드는 현재 `KeyboardDispatchProbe.Install()`을 호출한다. 따라서 다른 IME 실험 소스가 존재한다는 것만으로 조합 패치가 활성화되어 있다고 볼 수 없다.
- `WindowsImeProbe`는 GETMESSAGE/CALLWNDPROC 관찰 훅과 조합 조회 코드를 보유한다. 진단에 재사용할 수 있으나, 관찰만으로 기본 조합창이 숨겨지는 것은 아니다.
- `ImeProbe`에는 GUI 렌더링과 TextField 반환 문자열에 조합을 섞는 실험이 있다. 새 모드에서 같은 메서드를 패치할 경우 Harmony 충돌 여부를 확인한다.
- 해당 실험의 성공 여부나 현재 로드 상태는 검증하지 않았다.

공통 GUI 패치는 그 공통 경로를 사용하는 바닐라·모드 입력창에 적용된다. TMP/custom editor/native ImGui InputText 등 별도 구현까지 모든 입력창에 자동 적용된다고 보장하지 않는다. 별도 구현은 조합/포커스 브리지에 연결하는 어댑터가 필요할 수 있다.

## 7. 실제 게임 검증과 중단 기준

1. 한국어 상태 + 입력창 없음: W/A/S/D 눌림/유지/해제, 반복, 단축키가 정상이며 OS 조합창과 미사용 조합이 생기지 않는지 확인.
2. 입력창 클릭: 한/영 상태가 보존되고 `ㅇ → 아 → 안 → 안ㄴ`이 즉시 표시되는지 확인. 확정 `안`은 한 번만 삽입.
3. 선택 영역 덮어쓰기, 중간 삽입, 받침 분리, `ㅋㅋ`, 같은 음절 반복, 조합 Backspace, Escape 취소, Enter 확정 확인.
4. 조합 중 다른 필드 클릭/모달 닫기/Alt+Tab: 이전 글자가 새 필드에 입력되지 않고 키가 눌린 상태로 남지 않는지 확인.
5. TextArea 줄바꿈·스크롤, 검색·이름·정규식·숫자·최대 길이·비밀번호, 한자 후보 선택 확인.
6. UI 배율 및 Windows DPI, 창 모드/보더리스 전환 확인.
7. Windows 메시지, 실제 두 GLFW 입력 콜백, InputSystem 상태, GUI 포커스를 함께 기록. 로그에는 기본적으로 입력한 실제 문자열을 남기지 않는다.

조합을 읽을 수 없으면 OS 조합창 숨김을 켜지 않는다. 직접 확정 경로가 실패하면 해당 경로를 인수하지 않는다. 문자 유실/중복/창 크래시가 나타나면 브리지를 해제하고 기존 동작으로 복구한다. 현재 Windows 한국어 IME의 IMM 호환 경로로 충분하지 않을 때에만 TSF 구현을 다음 단계로 검토한다.

## 확인한 산출물

`.research/`에는 설치 DLL에서 뽑은 GUI/Input/Keyboard/InputRuntime/InputSystem/RawKeys/TextFocus, RimWorld GlfwInputBridge/ImguiDispatch/Bootstrap, Brutal Glfw/GlfwWindow와 GLFW disassembly를 보관했다. 로컬 ILSpy 배포 ZIP의 Decompiler 라이브러리로 추출했다. 추출 도구 빌드는 경고·오류 0개이며, 이는 IME 패치 검증을 의미하지 않는다.

실제 DLL SHA256:

```text
glfw3.dll     4D029C7F18C2A6344B37F1AB8860CB9386757CE33B2C50FFA53AFFAB96FD66DB
Waylith.dll   B3A0910C2763407DD3934842BED84BA717F37C865E7CF63D68AFA6AE173AC956
UnityStubs.dll B0B997C300670D5A42EA70F468FF9FEDABA03CBD65EABF5D522675B3F16CDB22
RimWorld.dll  7183C5B884BF22DFE9EFA10E596FFAC862EEB3C71678B544C357ED69F50030A3
```

게임 DLL과 기존 모드는 변경하지 않았다. 설치 가능한 패치 구현, 실제 Windows IME 및 게임 입력 검증은 미실시다.
