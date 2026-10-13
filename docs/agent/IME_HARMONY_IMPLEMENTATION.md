# IME Harmony 구현 결과

2026-10-13. 구현 기준은 `IME_HARMONY_INVESTIGATION.md`입니다.

## 구현 계약

- `Bootstrap.OnPrePollEvents` Postfix에서 실제 GLFW HWND를 받아 같은 스레드에 서브클래스를 설치합니다. `OnAppShuttingDown`과 `WM_NCDESTROY`에서 해제합니다.
- 포커스는 실제 `TextFieldImpl` 내부의 `TrackControlWindow` 호출로 확인한 ID에 연결합니다. 별도로 ID를 생성하지 않습니다. GUI disabled, 읽기 전용, PasswordField/secureText/maskChar는 제외합니다.
- 포커스가 바뀌거나 사라지면 미확정 조합을 취소합니다. 이미 확정된 이벤트는 이전 소유자에 전달하고 새 키보드 포커스를 복원합니다. 이전 필드가 호출되지 않으면 그 필드에 대한 편집을 실행하지 않습니다.
- `WM_IME_STARTCOMPOSITION/COMPOSITION/ENDCOMPOSITION`을 인수합니다. RESULTSTR를 InputSystem의 기존 문자 큐에 넣고 기본 IME 문자 생성을 차단합니다. 일반 GLFW 문자는 같은 큐와 원래 Keyboard 문자 콜백을 거칩니다. 소유자 메타데이터는 이벤트 순서로 연결하며 문자열 비교로 중복 제거하지 않습니다.
- `WM_IME_SETCONTEXT`에서는 기본 조합창 비트만 제거합니다. 후보창을 유지하고 기존 GUI backend의 좌표 변환/레이아웃/스크롤과 framebuffer 대비 client 비율로 후보 좌표를 계산합니다.
- 조합 미리보기는 원래 DrawTextFieldChrome/TextArea 렌더러를 사용합니다. 임시 caret/anchor/scroll은 Finalizer에서 복구합니다. 반환값과 TextEditor에는 미확정 문자열을 넣지 않습니다. 외부 텍스트 변경은 기존 조합을 취소합니다.
- VK_PROCESSKEY 제어키 또는 조합 메시지로 처리된 제어키의 keyDown만 막습니다. keyUp과 별도 일반 키는 원래 경로를 유지합니다.
- 조회 실패 시 OS 표시를 인수하지 않습니다. 설치/실행 오류는 브리지를 해제하고 borrowed HIMC를 반환하며, 원래 문자 수신을 유지합니다. IMM_ERROR_NODATA와 오류를 구분합니다.

## 검증

- 설치 RimWorld DLL 참조 빌드: 경고 0, 오류 0.
- 실제 게임 DLL의 Harmony 15개 훅 적용 및 해제 확인.
- 실제 GUI 편집·렌더링 코드와 대역 backend를 사용한 smoke: 선택 대체/내부 커서, 저장·반환/Editor 불변, 연속 결과와 동일 문자, IME/영문 큐 순서, 이전 소유 필드로 확정/새 포커스 복원, 다음 조합, 외부 변경 취소, 비밀번호 제외, 제어키/keyUp 구분, 렌더링 예외 복구 통과.
- 숨겨진 Windows 창 생성은 가능하나 현재 테스트 프로세스에서 `ImmCreateContext()`가 0을 반환했습니다. 컨텍스트 detach/restore 검사는 SKIP으로 표시하며 검증된 것으로 간주하지 않습니다.
- 실제 한국어 IME 메시지, 한/영 상태 유지, 문자 유실/중복, W/A/S/D 유지·해제, Alt+Tab/모달/창 재생성, TextArea 시각적 줄바꿈, 한자 후보창, UI 배율/DPI는 실게임 확인이 필요합니다. 조사 문서 7절을 사용합니다.

게임 설치 폴더 및 기존 IMEProbe 모드는 수정하지 않았습니다. 빌드 산출물은 저장소 `1.7/Assemblies/Template.dll`입니다.
