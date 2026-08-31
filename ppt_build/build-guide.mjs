import fs from "node:fs/promises";
import { Presentation, PresentationFile } from "@oai/artifact-tool";

const OUT = "C:/Users/JeungKiHong/Codex/Backup/artifacts/PB_설치·사용가이드.pptx";
const PREVIEW = "C:/Users/JeungKiHong/Codex/Backup/ppt_build/preview";
const W = 1280;
const H = 720;
const GREEN = "#2E9B62";
const DARK = "#17202A";
const GRAY = "#5E6872";
const LIGHT = "#F1F4F2";
const RULE = "#CBD4CE";

async function writeBlob(path, blob) {
  await fs.writeFile(path, new Uint8Array(await blob.arrayBuffer()));
}

function box(slide, left, top, width, height, fill = "none", line = "none", radius = "square") {
  return slide.shapes.add({
    geometry: radius === "round" ? "roundRect" : "rect",
    position: { left, top, width, height },
    fill,
    line: { style: "solid", fill: line, width: line === "none" ? 0 : 1 },
    ...(radius === "round" ? { borderRadius: "rounded-xl" } : {}),
  });
}

function text(slide, value, left, top, width, height, style = {}) {
  const shape = slide.shapes.add({
    geometry: "textbox",
    position: { left, top, width, height },
    fill: "none",
    line: { style: "solid", fill: "none", width: 0 },
  });
  shape.text = value;
  shape.text.style = {
    fontFamily: "Arial",
    fontSize: 20,
    color: DARK,
    ...style,
  };
  return shape;
}

function rule(slide, left, top, width, color = RULE, height = 2) {
  box(slide, left, top, width, height, color, "none");
}

function header(slide, kicker, title, page) {
  text(slide, kicker.toUpperCase(), 72, 46, 420, 24, { fontSize: 15, bold: true, color: GREEN, letterSpacing: 1 });
  text(slide, title, 72, 82, 1080, 60, { fontSize: 35, bold: true, color: DARK });
  rule(slide, 72, 154, 1136, RULE, 1);
  text(slide, `PB · 팀원용 안내  |  ${page}/12`, 72, 676, 1136, 20, { fontSize: 13, color: GRAY, alignment: "right" });
}

function notes(slide, extra = "") {
  slide.speakerNotes.textFrame.setText(`[Sources]\n- README.md\n- docs/INSTALL_GUIDE_KO.md\n- docs/KNOWN_ISSUES_KO.md\n${extra}[/Sources]`);
}

function processStep(slide, x, num, title, body, color = GREEN) {
  box(slide, x, 286, 250, 190, "#FFFFFF", RULE, "round");
  box(slide, x + 20, 308, 42, 42, color, "none", "round");
  text(slide, String(num), x + 20, 314, 42, 30, { fontSize: 21, bold: true, color: "#FFFFFF", alignment: "center" });
  text(slide, title, x + 20, 370, 210, 36, { fontSize: 23, bold: true });
  text(slide, body, x + 20, 414, 210, 48, { fontSize: 16, color: GRAY, lineSpacing: 1.1 });
}

function bulletList(slide, items, left, top, width, height, size = 21) {
  return text(slide, items.map((x) => `• ${x}`).join("\n"), left, top, width, height, { fontSize: size, color: DARK, lineSpacing: 1.35 });
}

async function main() {
  await fs.mkdir(PREVIEW, { recursive: true });
  const deck = Presentation.create({ slideSize: { width: W, height: H } });

  // 1
  {
    const s = deck.slides.add(); s.background.fill = "#FFFFFF";
    box(s, 0, 0, W, 16, GREEN, "none");
    text(s, "PB", 72, 86, 150, 88, { fontSize: 70, bold: true, color: GREEN });
    text(s, "팀원용 설치·사용 가이드", 72, 190, 920, 78, { fontSize: 50, bold: true });
    text(s, "Smart Switch 백업을 PC에서 안전하게 관리하고\n90일 경과 통화녹음을 검토하는 방법", 76, 292, 760, 88, { fontSize: 25, color: GRAY, lineSpacing: 1.2 });
    box(s, 860, 184, 280, 280, LIGHT, "none", "round");
    text(s, "PHONE\nBACKUP", 900, 254, 200, 90, { fontSize: 33, bold: true, color: GREEN, alignment: "center" });
    text(s, "설치 파일 전달은 Google Drive\n백업 데이터는 PC 로컬", 72, 594, 560, 36, { fontSize: 17, color: GRAY });
    text(s, "v1.5.0", 1070, 652, 130, 24, { fontSize: 16, color: GRAY, alignment: "right" });
    notes(s);
  }

  // 2
  {
    const s = deck.slides.add(); header(s, "01 · 전체 흐름", "PB는 백업 파일을 PC에서 관리합니다", 2);
    text(s, "Google Drive는 설치 파일을 전달하는 곳입니다. 음성·문서·사진 백업은 PC에만 남습니다.", 72, 190, 1080, 32, { fontSize: 22, color: GRAY });
    processStep(s, 92, 1, "Smart Switch", "폰을 USB로 연결\n백업 시작", "#6A8F7B");
    processStep(s, 382, 2, "PB 가져오기", "백업 회차를\n멤버에 등록", GREEN);
    processStep(s, 672, 3, "검증·색인", "파일 상태와\n통화 메타데이터 확인", "#508CBB");
    processStep(s, 962, 4, "검토·정리", "90일 경과 녹음만\n사용자 승인 후 삭제", "#B77A45");
    rule(s, 342, 380, 40, GREEN, 3); rule(s, 632, 380, 40, GREEN, 3); rule(s, 922, 380, 40, GREEN, 3);
    text(s, "연락처 동기화는 하지 않습니다. PB의 PC 연락처 메모는 휴대폰 주소록과 별개입니다.", 92, 548, 1000, 32, { fontSize: 20, bold: true, color: GREEN });
    notes(s);
  }

  // 3
  {
    const s = deck.slides.add(); header(s, "02 · 준비", "시작 전에 네 가지만 확인하세요", 3);
    const items = [
      ["PC", "Windows 10/11 x64\n쓰기 가능한 폴더"],
      ["폰", "Android 6.0 이상\nRelease APK"],
      ["연결", "같은 Wi‑Fi\n사설 네트워크 허용"],
      ["백업", "Smart Switch 설치\nUSB 케이블 준비"],
    ];
    items.forEach(([title, body], i) => { const x = 92 + i * 278; box(s, x, 226, 238, 184, i === 0 ? LIGHT : "#FFFFFF", RULE, "round"); text(s, title, x + 24, 252, 190, 32, { fontSize: 26, bold: true, color: GREEN }); text(s, body, x + 24, 306, 190, 56, { fontSize: 20, color: GRAY, lineSpacing: 1.2 }); });
    bulletList(s, ["Google Drive에는 APK·ZIP·PPT만 올립니다.", "실제 백업 원본은 Smart Switch와 PC 저장 폴더에 둡니다.", "처음 시험할 때는 원본 폰 파일을 삭제하지 않습니다."], 110, 480, 1000, 120, 20);
    notes(s);
  }

  // 4
  {
    const s = deck.slides.add(); header(s, "03 · Windows", "압축을 풀고 PhoneBackup.exe를 실행합니다", 4);
    text(s, "설치 프로그램이 아니라 포터블 배포본입니다.", 72, 190, 600, 32, { fontSize: 23, color: GRAY });
    box(s, 90, 260, 410, 250, LIGHT, "none", "round");
    text(s, "01", 126, 298, 70, 44, { fontSize: 34, bold: true, color: GREEN });
    text(s, "ZIP 전체 압축 해제", 126, 352, 300, 35, { fontSize: 25, bold: true });
    text(s, "문서 등 쓰기 가능한 폴더에\n폴더 전체를 풀어 둡니다.", 126, 400, 300, 62, { fontSize: 20, color: GRAY });
    box(s, 560, 260, 410, 250, "#FFFFFF", RULE, "round");
    text(s, "02", 596, 298, 70, 44, { fontSize: 34, bold: true, color: GREEN });
    text(s, "PhoneBackup.exe 실행", 596, 352, 300, 35, { fontSize: 25, bold: true });
    text(s, "SmartScreen이 나오면\n출처 확인 후 ‘추가 정보 → 실행’을 누릅니다.", 596, 400, 320, 62, { fontSize: 20, color: GRAY });
    text(s, "포터블 ZIP 안의 실행 파일을 사용하세요. 예전에 받은 단일 EXE와 섞지 않습니다.", 90, 572, 900, 30, { fontSize: 19, bold: true, color: GREEN });
    notes(s);
  }

  // 5
  {
    const s = deck.slides.add(); header(s, "04 · Android", "Google Drive APK는 한 번만 권한을 허용해 설치합니다", 5);
    bulletList(s, ["Drive에서 PhoneBackupAndroid-release-v1.5.0.apk를 내려받습니다.", "파일을 여는 앱에 ‘알 수 없는 앱 설치’를 일시 허용합니다.", "설치 후 해당 권한은 다시 끄는 것을 권장합니다.", "기존 Debug 앱 때문에 업데이트가 안 되면 기존 PB를 삭제하고 Release APK를 설치합니다."], 92, 218, 700, 230, 21);
    box(s, 860, 222, 280, 260, LIGHT, "none", "round");
    text(s, "설치 순서", 900, 254, 200, 30, { fontSize: 24, bold: true, color: GREEN, alignment: "center" });
    text(s, "다운로드\n↓\n권한 허용\n↓\n설치\n↓\nPB 실행", 920, 300, 160, 150, { fontSize: 23, bold: true, alignment: "center", lineSpacing: 1.15 });
    text(s, "Release APK 설치 후 PC 연결과 백업 폴더 권한을 다시 등록할 수 있습니다.", 92, 548, 1000, 32, { fontSize: 19, color: GRAY });
    notes(s);
  }

  // 6
  {
    const s = deck.slides.add(); header(s, "05 · 연결", "PC에서 멤버를 만든 뒤 6자리 코드로 폰을 연결합니다", 6);
    text(s, "폰마다 멤버를 따로 선택해야 파일이 섞이지 않습니다.", 72, 190, 900, 32, { fontSize: 23, color: GRAY });
    const steps = [["PC", "멤버·기기 탭 → 새 멤버"], ["PC", "멤버 선택 → 기기 연결"], ["폰", "6자리 코드 입력 → PC 찾기"], ["확인", "PC 연결 완료 표시"]];
    steps.forEach(([tag, body], i) => { const y = 254 + i * 82; box(s, 118, y, 110, 54, tag === "폰" ? "#E8F3F8" : LIGHT, "none", "round"); text(s, tag, 118, y + 14, 110, 26, { fontSize: 19, bold: true, color: GREEN, alignment: "center" }); text(s, body, 270, y + 10, 700, 35, { fontSize: 22, bold: i === 3 }); if (i < 3) rule(s, 171, y + 57, 2, GREEN, 25); });
    text(s, "USB 등록을 사용하는 경우에도 최초 등록 후에는 USB를 분리하고 Wi‑Fi로 사용할 수 있습니다.", 118, 594, 980, 30, { fontSize: 18, color: GRAY });
    notes(s);
  }

  // 7
  {
    const s = deck.slides.add(); header(s, "06 · 백업", "Smart Switch 백업 후 PB에서 회차를 가져옵니다", 7);
    const rows = [["1", "Smart Switch PC 열기", "폰을 USB로 연결하고 Smart Switch에서 백업을 시작합니다."], ["2", "PB → Smart Switch 가져오기", "멤버를 선택한 뒤 백업 회차 폴더를 지정합니다."], ["3", "PB 등록 완료", "PB가 원본을 수정하지 않고 자체 저장소에 복사합니다."]];
    rows.forEach(([n, title, body], i) => { const y = 222 + i * 110; box(s, 92, y, 80, 64, GREEN, "none", "round"); text(s, n, 92, y + 15, 80, 30, { fontSize: 26, bold: true, color: "#FFFFFF", alignment: "center" }); text(s, title, 216, y + 4, 460, 34, { fontSize: 25, bold: true }); text(s, body, 216, y + 42, 790, 32, { fontSize: 18, color: GRAY }); });
    text(s, "PB는 통화녹음 파일명·날짜·전화번호를 색인하고, 일반 파일은 별도 탭으로 분류합니다.", 92, 584, 1050, 32, { fontSize: 20, bold: true, color: GREEN });
    notes(s);
  }

  // 8
  {
    const s = deck.slides.add(); header(s, "07 · 확인", "가져오기 완료 후 목록과 검증 상태를 확인합니다", 8);
    box(s, 92, 220, 510, 250, LIGHT, "none", "round");
    text(s, "통화 녹음", 124, 252, 220, 32, { fontSize: 25, bold: true, color: GREEN });
    text(s, "모바일 · 분류 · 녹음일\n대상 · 상대방 · 소속\n원본 파일명 · 상태 · 폴더", 124, 312, 380, 100, { fontSize: 21, lineSpacing: 1.25 });
    box(s, 650, 220, 510, 250, "#FFFFFF", RULE, "round");
    text(s, "일반 파일", 682, 252, 220, 32, { fontSize: 25, bold: true, color: GREEN });
    text(s, "모바일 · 분류 · 수정일\n원본 파일명 · 상태 · 폴더\n문서·사진·영상·기타", 682, 312, 380, 100, { fontSize: 21, lineSpacing: 1.25 });
    text(s, "대시보드에서 최근 백업 시각과 실패 수를 확인하고, 검증 완료 상태가 될 때까지 원본을 지우지 않습니다.", 92, 548, 1060, 50, { fontSize: 20, bold: true, color: GREEN });
    notes(s);
  }

  // 9
  {
    const s = deck.slides.add(); header(s, "08 · 90일 삭제", "90일 삭제는 검증된 통화녹음만 사용자 승인 후 실행됩니다", 9);
    box(s, 92, 222, 1068, 90, LIGHT, "none", "round");
    text(s, "자동 삭제 아님", 126, 252, 220, 32, { fontSize: 26, bold: true, color: "#B65F36" });
    text(s, "사용자가 ‘90일 삭제 검토’를 누르고 확인해야 시작됩니다.", 382, 254, 700, 30, { fontSize: 22, bold: true });
    const checks = ["Smart Switch 검증 완료", "PB 백업·해시 검증 완료", "녹음일 기준 90일 경과", "휴대폰에서 경로·해시 재검사", "불일치 파일은 건너뜀"];
    checks.forEach((v, i) => { const y = 356 + i * 46; text(s, "✓", 140, y, 36, 28, { fontSize: 24, bold: true, color: GREEN }); text(s, v, 192, y, 820, 28, { fontSize: 21 }); });
    text(s, "현재 일반 문서·사진·영상은 모바일 90일 삭제 대상이 아닙니다. PC에서 선택 삭제할 수 있습니다.", 92, 604, 1060, 30, { fontSize: 18, color: GRAY });
    notes(s);
  }

  // 10
  {
    const s = deck.slides.add(); header(s, "09 · 문제 대응", "연결이 끊겨도 원본을 먼저 건드리지 않습니다", 10);
    const left = ["Wi‑Fi 확인", "PC 앱 실행 여부", "Smart Switch 완료 여부", "마지막 백업 시각"]; const right = ["앱을 다시 열기", "새 연결 코드 발급", "PB에서 가져오기 재시도", "원본 파일 삭제 금지"];
    text(s, "먼저 확인", 92, 214, 300, 34, { fontSize: 25, bold: true, color: GREEN });
    text(s, "다음 조치", 662, 214, 300, 34, { fontSize: 25, bold: true, color: GREEN });
    left.forEach((v, i) => { const y = 278 + i * 64; box(s, 92, y, 430, 44, LIGHT, "none", "round"); text(s, v, 118, y + 9, 360, 26, { fontSize: 20 }); });
    right.forEach((v, i) => { const y = 278 + i * 64; box(s, 662, y, 430, 44, "#FFFFFF", RULE, "round"); text(s, v, 688, y + 9, 360, 26, { fontSize: 20 }); });
    text(s, "백업 중 앱이 닫혔다면 재실행 후 진행 상태를 확인합니다. 이미 전송된 파일은 해시 기준으로 중복 저장되지 않습니다.", 92, 574, 1050, 50, { fontSize: 19, bold: true, color: GREEN });
    notes(s);
  }

  // 11
  {
    const s = deck.slides.add(); header(s, "10 · 오류 리포트", "오류가 나면 리포트 ZIP 하나만 Codex에 첨부합니다", 11);
    box(s, 92, 224, 470, 280, LIGHT, "none", "round");
    text(s, "PC", 128, 258, 120, 32, { fontSize: 27, bold: true, color: GREEN });
    text(s, "설정 → 오류 리포트 내보내기\n→ PB-error-report-날짜시간.zip", 128, 322, 360, 72, { fontSize: 22, lineSpacing: 1.2 });
    box(s, 610, 224, 470, 280, "#FFFFFF", RULE, "round");
    text(s, "모바일", 646, 258, 160, 32, { fontSize: 27, bold: true, color: GREEN });
    text(s, "오류 리포트 공유/저장\n→ 파일 관리자 또는 Drive에 저장", 646, 322, 360, 72, { fontSize: 22, lineSpacing: 1.2 });
    text(s, "Codex 대화에 ZIP 첨부 + ‘PB 오류 분석’이라고 입력", 92, 570, 1000, 38, { fontSize: 24, bold: true, color: GREEN });
    text(s, "파일 내용·연락처·인증 토큰은 포함하지 않습니다. PC 앱이 열리지 않으면 %TEMP%\\PhoneBackup-*.log를 진단 도우미로 수집합니다.", 92, 624, 1080, 28, { fontSize: 16, color: GRAY });
    notes(s);
  }

  // 12
  {
    const s = deck.slides.add(); header(s, "11 · 완료 확인", "팀원은 이 체크리스트만 따라 하면 됩니다", 12);
    const checks = ["PC ZIP 압축 해제 후 PhoneBackup.exe 실행", "Android Release APK 설치", "PC에서 멤버 생성 및 6자리 연결", "Smart Switch 백업 완료", "PB 가져오기·검증 완료", "통화녹음·일반 파일 목록 확인", "삭제는 90일 조건과 사용자 확인 후 실행", "오류 시 리포트 ZIP을 Codex에 첨부"];
    checks.forEach((v, i) => { const col = i < 4 ? 0 : 1; const row = i % 4; const x = col === 0 ? 110 : 650; const y = 220 + row * 76; text(s, "□", x, y, 36, 32, { fontSize: 27, color: GREEN }); text(s, v, x + 54, y + 2, 460, 40, { fontSize: 20 }); });
    rule(s, 110, 566, 1000, GREEN, 2);
    text(s, "PB 백업 데이터는 PC 로컬에 보관하고, 원본 삭제는 항상 마지막 단계로 미룹니다.", 110, 594, 1000, 34, { fontSize: 22, bold: true, color: GREEN });
    notes(s);
  }

  for (const [index, slide] of deck.slides.items.entries()) {
    const stem = `slide-${String(index + 1).padStart(2, "0")}`;
    await writeBlob(`${PREVIEW}/${stem}.png`, await deck.export({ slide, format: "png", scale: 1 }));
  }
  await writeBlob(`${PREVIEW}/montage.webp`, await deck.export({ format: "webp", montage: true, scale: 1 }));
  const pptx = await PresentationFile.exportPptx(deck);
  await pptx.save(OUT);
}

main().catch((error) => { console.error(error); process.exitCode = 1; });
