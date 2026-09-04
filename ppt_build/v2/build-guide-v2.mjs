import fs from "node:fs/promises";
import { Presentation, PresentationFile } from "@oai/artifact-tool";

const OUT = "C:/Users/JeungKiHong/Codex/Backup/artifacts/PB_2.0_설치·사용가이드.pptx";
const RENDER = "C:/Users/JeungKiHong/Codex/Backup/ppt_build/v2/rendered";
const W = 1280, H = 720;
const GREEN = "#16835A", GREEN_DARK = "#0F5F42", GREEN_SOFT = "#E8F5EF";
const INK = "#18211D", MUTED = "#64716A", LINE = "#DDE5E0", CANVAS = "#F5F7F6";
const DANGER = "#B42318", AMBER = "#8A5A00", WHITE = "#FFFFFF";

async function writeBlob(path, blob) { await fs.writeFile(path, new Uint8Array(await blob.arrayBuffer())); }
function rect(slide, x, y, w, h, fill = WHITE, line = "none", radius = "square") {
  return slide.shapes.add({ geometry: radius === "round" ? "roundRect" : "rect", position: { left:x, top:y, width:w, height:h }, fill,
    line: { style:"solid", fill:line, width:line === "none" ? 0 : 1 }, ...(radius === "round" ? { borderRadius:"rounded-xl" } : {}) });
}
function txt(slide, value, x, y, w, h, style = {}) {
  const shape = slide.shapes.add({ geometry:"textbox", position:{left:x,top:y,width:w,height:h}, fill:"none", line:{style:"solid",fill:"none",width:0} });
  shape.text = value; shape.text.style = { fontFamily:"Malgun Gothic", fontSize:20, color:INK, ...style }; return shape;
}
function line(slide, x, y, w, fill = LINE, h = 2) { rect(slide,x,y,w,h,fill,"none"); }
function header(slide, kicker, title, page) {
  txt(slide,kicker,72,42,420,24,{fontSize:15,bold:true,color:GREEN,letterSpacing:1});
  txt(slide,title,72,76,1120,62,{fontSize:35,bold:true}); line(slide,72,151,1136,LINE,1);
  txt(slide,`PB 2.0 · 독립형 사용 가이드  |  ${page}/10`,72,676,1136,20,{fontSize:13,color:MUTED,alignment:"right"});
}
function notes(slide) { slide.speakerNotes.textFrame.setText("[Sources]\n- README.md\n- docs/INSTALL_GUIDE_KO.md\n- docs/KNOWN_ISSUES_KO.md\n[/Sources]"); }
function step(slide, n, title, body, x, y, w=310) {
  txt(slide,String(n).padStart(2,"0"),x,y,52,38,{fontSize:27,bold:true,color:GREEN});
  txt(slide,title,x+68,y,w-68,36,{fontSize:24,bold:true});
  txt(slide,body,x+68,y+43,w-68,68,{fontSize:17,color:MUTED,lineSpacing:1.2});
}
function check(slide, value, x, y, color=GREEN) { txt(slide,"✓",x,y,30,28,{fontSize:23,bold:true,color}); txt(slide,value,x+42,y,470,32,{fontSize:19}); }

async function main() {
  await fs.mkdir(RENDER,{recursive:true});
  const deck = Presentation.create({slideSize:{width:W,height:H}});

  { const s=deck.slides.add(); s.background.fill=WHITE; rect(s,0,0,18,H,GREEN,"none");
    txt(s,"PB",76,84,180,92,{fontSize:72,bold:true,color:GREEN});
    txt(s,"백업은 Smart Switch로,\n정리는 더 안전하고 단순하게",76,205,900,142,{fontSize:48,bold:true,lineSpacing:1.08});
    txt(s,"PC와 모바일이 서로 연결되지 않는 PB 2.0 사용 가이드",80,384,850,36,{fontSize:24,color:MUTED});
    line(s,80,482,600,GREEN,3); txt(s,"Windows 10/11  ·  Android 6.0 이상",80,510,700,28,{fontSize:18,color:GREEN_DARK,bold:true});
    txt(s,"v2.0.0",1050,650,150,24,{fontSize:16,color:MUTED,alignment:"right"}); notes(s); }

  { const s=deck.slides.add(); s.background.fill=CANVAS; header(s,"01 · 구조","PC와 모바일은 각자 맡은 일만 합니다",2);
    txt(s,"PC",92,207,180,48,{fontSize:34,bold:true,color:GREEN}); txt(s,"Smart Switch 백업 관리",92,270,430,38,{fontSize:26,bold:true});
    txt(s,"백업 폴더 자동 탐색\n파일 유형별 분류와 검색\n파일 열기·탐색기 바로가기\nSHA-256 무결성 확인",92,332,430,180,{fontSize:21,lineSpacing:1.35});
    rect(s,623,205,2,345,LINE,"none");
    txt(s,"모바일",704,207,220,48,{fontSize:34,bold:true,color:GREEN}); txt(s,"저장공간 점검과 정리",704,270,430,38,{fontSize:26,bold:true});
    txt(s,"전체 용량 위험 알림\n90일 지난 통화녹음 검토\n사진 미리보기·다중 선택\n백업 확인 전 삭제 잠금",704,332,430,180,{fontSize:21,lineSpacing:1.35});
    rect(s,92,578,1042,54,GREEN_SOFT,"none","round"); txt(s,"Wi‑Fi · 방화벽 · 6자리 코드 · 연락처 동기화가 필요 없습니다",116,592,990,28,{fontSize:21,bold:true,color:GREEN_DARK,alignment:"center"}); notes(s); }

  { const s=deck.slides.add(); s.background.fill=WHITE; header(s,"02 · PC 시작","Smart Switch 백업 후 PB가 폴더를 찾아 정리합니다",3);
    step(s,1,"Smart Switch 백업","휴대폰을 USB로 연결하고\nSmart Switch에서 백업 완료",86,222,330);
    step(s,2,"백업 자동 찾기","PB 상단의 ‘백업 자동 찾기’\n기종과 위치를 자동 인식",472,222,330);
    step(s,3,"목록 확인","홈의 파일 수와 용량 확인\n유형별 탭에서 필요한 파일 탐색",858,222,330);
    line(s,86,390,1100,LINE,1);
    txt(s,"못 찾는 경우",86,426,240,34,{fontSize:24,bold:true,color:AMBER});
    txt(s,"‘폴더 직접 추가’에서 정확한 회차가 아니라 Smart Switch 백업의 상위 폴더를 선택하세요.\nPB가 그 아래의 SM-* 기종 폴더를 다시 나눕니다.",86,476,1030,82,{fontSize:21,lineSpacing:1.25});
    txt(s,"PB는 원본을 복사하지 않아 저장공간을 이중으로 쓰지 않습니다.",86,596,1030,28,{fontSize:19,bold:true,color:GREEN_DARK}); notes(s); }

  { const s=deck.slides.add(); s.background.fill=CANVAS; header(s,"03 · PC 파일 찾기","파일 유형과 통화 상대 기준으로 바로 찾습니다",4);
    txt(s,"통화녹음",86,207,250,40,{fontSize:28,bold:true,color:GREEN});
    txt(s,"날짜별 / 상대방별 그룹",86,270,430,32,{fontSize:23,bold:true});
    txt(s,"파일명에서 녹음일, 전화번호, 상대방,\n병원·의사 대상과 소속을 분리해 표시",86,320,470,76,{fontSize:19,color:MUTED,lineSpacing:1.25});
    txt(s,"사진 · 영상 · 문서 · 오디오",676,207,500,40,{fontSize:28,bold:true,color:GREEN});
    txt(s,"각 유형을 별도 탭으로 관리",676,270,430,32,{fontSize:23,bold:true});
    txt(s,"사진은 선택 즉시 미리보기\n삼성 전용 데이터와 알 수 없는 파일도 숨기지 않음",676,320,470,76,{fontSize:19,color:MUTED,lineSpacing:1.25});
    line(s,86,460,1098,LINE,1);
    check(s,"더블클릭: 기본 프로그램으로 파일 열기",100,500); check(s,"폴더에서 보기: 탐색기에서 해당 파일 자동 선택",100,548);
    check(s,"검색: 파일명·경로·전화번호·상대방·소속",660,500); check(s,"삼성 데이터: 내용 해제 없이 목록과 용량만 표시",660,548); notes(s); }

  { const s=deck.slides.add(); s.background.fill=WHITE; header(s,"04 · PC 안전 원칙","Smart Switch 백업 폴더는 읽기 전용으로 관리합니다",5);
    txt(s,"무결성 확인",86,218,350,38,{fontSize:28,bold:true,color:GREEN});
    txt(s,"등록 당시 크기·수정일과 비교한 뒤\nSHA-256 해시를 계산합니다.",86,278,450,74,{fontSize:21,lineSpacing:1.3});
    txt(s,"확인할 수 있는 것",86,398,300,32,{fontSize:23,bold:true});
    check(s,"현재 PC 파일을 다시 읽을 수 있음",86,450); check(s,"등록 후 바뀌거나 사라진 파일",86,495);
    rect(s,650,216,470,340,"#FFF7E8","none","round");
    txt(s,"개별 삭제 버튼이 없는 이유",690,254,390,36,{fontSize:25,bold:true,color:AMBER});
    txt(s,"Smart Switch 백업 내부 파일을\n임의로 지우면 전체 복원이 실패할 수 있습니다.\n\n‘목록에서 제거’는 PB 색인만 지우고\n원본에는 손대지 않습니다.",690,320,380,166,{fontSize:21,lineSpacing:1.3});
    txt(s,"중요한 백업은 별도 외장 저장소에도 2차 보관하세요.",86,596,1030,30,{fontSize:19,bold:true,color:GREEN_DARK}); notes(s); }

  { const s=deck.slides.add(); s.background.fill=CANVAS; header(s,"05 · 모바일 현황","저장공간 위험과 정리 대상을 한 화면에서 봅니다",6);
    txt(s,"저장공간",86,218,240,40,{fontSize:28,bold:true,color:GREEN});
    rect(s,86,282,530,12,"#E1E7E3","none","round"); rect(s,86,282,445,12,GREEN,"none","round");
    txt(s,"사용률과 남은 용량",86,316,420,32,{fontSize:23,bold:true}); txt(s,"위험 기준을 80% · 85% · 90% 중 선택",86,360,500,30,{fontSize:19,color:MUTED});
    txt(s,"파일 검사",690,218,240,40,{fontSize:28,bold:true,color:GREEN});
    check(s,"통화녹음과 일반 오디오",690,286); check(s,"사진·영상",690,334); check(s,"문서와 제조사 폴더",690,382);
    line(s,86,468,1098,LINE,1);
    txt(s,"파일이 안 보이면",86,505,260,34,{fontSize:24,bold:true,color:AMBER});
    txt(s,"‘검토 폴더 추가’에서 통화녹음·문서 폴더를 한 번 선택합니다.\nAndroid가 허용한 범위만 PB가 읽고 정리합니다.",360,501,760,78,{fontSize:21,lineSpacing:1.25}); notes(s); }

  { const s=deck.slides.add(); s.background.fill=WHITE; header(s,"06 · 삭제 잠금","백업 완료를 확인하기 전에는 아무것도 지울 수 없습니다",7);
    step(s,1,"목록 고정","‘백업 준비 시작’으로\n현재 파일의 익명 해시 저장",78,220,260);
    step(s,2,"Smart Switch","USB로 PC에 연결해\n백업 완료 화면 확인",360,220,260);
    step(s,3,"직접 확인","완료·오류 없음·PC 폴더 유지\n세 항목을 직접 체크",642,220,260);
    step(s,4,"24시간 허용","고정 목록에 있던 파일만\n제한적으로 삭제 가능",924,220,260);
    rect(s,78,412,1100,112,GREEN_SOFT,"none","round");
    txt(s,"자동 확인이 아닌 이유",110,438,300,32,{fontSize:24,bold:true,color:GREEN_DARK});
    txt(s,"회사 PC 보안을 우회하지 않기 위해 모바일은 Smart Switch 결과를 읽지 않습니다.\n체크리스트는 사용자가 실제 완료 화면을 확인했다는 기록입니다.",410,432,720,66,{fontSize:19,lineSpacing:1.25});
    txt(s,"백업하지 않고 확인만 누르면 복구할 PC 사본이 없습니다.",78,580,1100,32,{fontSize:21,bold:true,color:DANGER,alignment:"center"}); notes(s); }

  { const s=deck.slides.add(); s.background.fill=CANVAS; header(s,"07 · 모바일 정리","통화녹음은 자동 후보, 일반 파일은 직접 선택합니다",8);
    txt(s,"통화녹음",86,214,260,40,{fontSize:28,bold:true,color:GREEN});
    txt(s,"90일 초과만",86,276,430,42,{fontSize:30,bold:true});
    txt(s,"오래된 순으로 표시\n수정일이 없는 파일은 후보 제외\n전체 선택 또는 개별 선택",86,342,430,120,{fontSize:21,lineSpacing:1.35});
    rect(s,620,210,2,370,LINE,"none");
    txt(s,"일반 파일",690,214,260,40,{fontSize:28,bold:true,color:GREEN});
    txt(s,"보고 직접 선택",690,276,430,42,{fontSize:30,bold:true});
    txt(s,"사진은 미리보기 제공\n영상·문서·오디오·기타 탭\n경로·날짜·용량을 확인 후 다중 선택",690,342,430,120,{fontSize:21,lineSpacing:1.35});
    rect(s,86,546,1030,62,"#FFF1EF","none","round"); txt(s,"마지막 경고와 Android 시스템 승인 후에만 삭제됩니다",110,562,980,32,{fontSize:22,bold:true,color:DANGER,alignment:"center"}); notes(s); }

  { const s=deck.slides.add(); s.background.fill=WHITE; header(s,"08 · 매주 한 번","알림이 오면 같은 순서로 짧게 점검합니다",9);
    txt(s,"1",104,230,60,60,{fontSize:42,bold:true,color:GREEN}); txt(s,"저장공간 사용률 확인",184,238,430,38,{fontSize:27,bold:true});
    txt(s,"2",104,334,60,60,{fontSize:42,bold:true,color:GREEN}); txt(s,"필요하면 Smart Switch 백업",184,342,430,38,{fontSize:27,bold:true});
    txt(s,"3",104,438,60,60,{fontSize:42,bold:true,color:GREEN}); txt(s,"PB에서 오래된 파일만 정리",184,446,430,38,{fontSize:27,bold:true});
    rect(s,720,214,420,310,GREEN_SOFT,"none","round");
    txt(s,"삭제되지 않는 파일",760,252,340,34,{fontSize:25,bold:true,color:GREEN_DARK});
    check(s,"백업 준비 목록에 없던 파일",760,320); check(s,"스냅샷 이후 변경된 파일",760,370); check(s,"90일 이내 통화녹음",760,420); check(s,"수정일을 확인할 수 없는 녹음",760,470);
    txt(s,"앱을 강제 종료하면 알림이 지연될 수 있습니다.",104,582,1000,30,{fontSize:19,color:MUTED}); notes(s); }

  { const s=deck.slides.add(); s.background.fill=CANVAS; header(s,"09 · 문제 해결","오류가 나면 원본을 지우지 말고 리포트를 남깁니다",10);
    txt(s,"PC",86,208,120,38,{fontSize:28,bold:true,color:GREEN});
    check(s,"설정·도움말 → 오류 리포트 내보내기",86,270); check(s,"폴더가 안 보이면 ‘폴더 직접 추가’",86,318); check(s,"파일 없음은 원본 이동 여부 확인",86,366);
    txt(s,"모바일",674,208,160,38,{fontSize:28,bold:true,color:GREEN});
    check(s,"도움말 → 오류 리포트 저장",674,270); check(s,"파일이 안 보이면 저장소 권한·검토 폴더 확인",674,318); check(s,"삭제 실패는 Android 승인 또는 쓰기 권한 확인",674,366);
    line(s,86,450,1098,LINE,1);
    txt(s,"Codex에 제출",86,488,260,34,{fontSize:24,bold:true});
    txt(s,"PB-error-report-날짜시간.zip 첨부  +  “PB 오류 분석”",86,538,1050,42,{fontSize:27,bold:true,color:GREEN_DARK});
    txt(s,"리포트에는 파일 내용·원본 파일명·연락처·인증 정보가 포함되지 않습니다.",86,600,1050,28,{fontSize:18,color:MUTED}); notes(s); }

  for (const [index, slide] of deck.slides.items.entries()) {
    const stem=`slide-${String(index+1).padStart(2,"0")}`;
    await writeBlob(`${RENDER}/${stem}.png`,await deck.export({slide,format:"png",scale:1}));
    await fs.writeFile(`${RENDER}/${stem}.layout.json`,await (await slide.export({format:"layout"})).text());
  }
  await writeBlob(`${RENDER}/montage.webp`,await deck.export({format:"webp",montage:true,scale:1}));
  await (await PresentationFile.exportPptx(deck)).save(OUT);
}

main().catch(error=>{console.error(error);process.exitCode=1;});
