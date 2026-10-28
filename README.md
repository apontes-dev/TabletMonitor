# TabletMonitor

Transforma o **Samsung Galaxy Tab Pro 10.1 (SM-T520, "n2awifi")** — ou outro tablet Android 8+ — em **segundo monitor do PC (Windows) ou do Mac**, com toque, por **USB, Wi-Fi ou Bluetooth**, e deixa o tablet **dedicado só a essa função**.

**iPhone e iPad também funcionam**, sem App Store e sem conta de desenvolvedor: o PC/Mac serve um app web que se instala na Tela de Início (detalhes abaixo).

```
 PC Windows                                                Tablet
 ┌───────────────────────────────────────┐   USB (adb reverse)   ┌───────────────────────────┐
 │ monitor virtual (driver IDD)          │ ════════════════════► │ TabletMonitor             │
 │   ↓ captura (Desktop Duplication)     │   Wi-Fi (TCP 27183)   │   ↓ MediaCodec (hardware) │
 │ ffmpeg → H.264 (NVENC/AMF/QSV/x264)   │ ────────────────────► │ tela cheia                │
 │   ↓ fila com descarte → pacotes TMON  │   Bluetooth (RFCOMM)  │                           │
 │ TabletMonitor.exe ◄──── toques ───────┼ ┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄ ┤ toque → coordenadas       │
 │   ↓ InjectTouchInput (toque nativo)   │                       │                           │
 └───────────────────────────────────────┘                       └───────────────────────────┘
```

## O que vem no pacote

| Arquivo | Para quê |
|---|---|
| `TabletMonitor.exe` | App do Windows (um único .exe, não precisa instalar .NET). Traz o APK do tablet embutido. |
| `TabletMonitor-macOS-AppleSilicon.zip` | App do Mac para M1/M2/M3/M4 (leia o `LEIA-ME-INSTALAR.txt` dentro). |
| `TabletMonitor-macOS-Intel.zip` | App do Mac para Macs com Intel. |
| `TabletMonitor.apk` | App do tablet (o .exe instala sozinho; o avulso é para instalar à mão). |
| `TabletMonitorROM-SM-T520.zip` | Pacote para o **TWRP**: transforma a ROM do tablet num sistema dedicado (detalhes abaixo). |
| `TabletMonitorROM-SM-T520-semgoogle.zip` | Igual, removendo também Play Store e Play Services (mais leve). |
| `TabletMonitorROM-restaurar.zip` | Desfaz o pacote do TWRP, deixando o sistema exatamente como era. |
| `TabletMonitor-codigo-fonte.zip` | Código-fonte: `android/` (Kotlin), `desktop/` (C# .NET 8: Windows, macOS e cliente web), `rom/` (scripts do TWRP). |

Na primeira execução o app do computador baixa sozinho o **ffmpeg** e o **adb** (Windows: `%LOCALAPPDATA%\TabletMonitor\tools`; Mac: na pasta de suporte do app, em `~/Library`).

---

## Os três modos de conexão

O tablet tenta, nesta ordem, e troca sozinho quando um deles aparece ou some:

| Modo | Atraso (estimado) | Qualidade | Como ligar |
|---|---|---|---|
| **USB** | menor (~40–80 ms) | máxima (60 qps, 10 Mbps) | cabo + depuração USB. O app do Windows mantém o túnel `adb reverse`. O tablet carrega junto. |
| **Wi-Fi** | baixo (~60–120 ms) | máxima | mesma rede. O PC se anuncia; o tablet acha sozinho. 5 GHz ajuda muito. Durante a transmissão o tablet desliga a economia de energia do Wi-Fi. |
| **Bluetooth** | médio (~150–300 ms) | perfil leve: 15 qps, 1,5 Mbps, até 1280 px | parear tablet e PC uma vez. Bom para quando não há cabo nem rede; serve para textos, planilhas, chat — não para vídeo. |

Se a conexão não der conta (Wi-Fi ruim, Bluetooth), o PC **descarta quadros em vez de acumular atraso**: a imagem pode "pular", mas não fica segundos atrasada. Nos testes, mesmo com o link 8× mais lento que o vídeo, o atraso ficou limitado a ~1 s (sem o descarte ele passava de 6 s e continuava crescendo).

**Parear o Bluetooth (uma vez):** no tablet, toque e segure 3 s na tela de espera → **Parear Bluetooth com o PC**. No PC, abra *Configurações > Bluetooth e dispositivos > Adicionar dispositivo* ao mesmo tempo, escolha um no outro e confirme o código. Depois disso é automático.

## Resolução no SM-T520

A tela do SM-T520 tem 2560×1600, mas o decodificador de vídeo por hardware do Exynos 5420 não decodifica isso em tempo real. O app do tablet **descobre sozinho o maior tamanho que o decodificador aguenta** (na proporção 16:10 da tela, provavelmente perto de 1728×1080) e avisa o PC, que reduz a imagem para esse tamanho antes de comprimir.

Para textos nítidos, configure o monitor virtual com **o tamanho que o app sugere** (aparece em cinza no topo da janela do Windows quando o tablet conecta) e escala de 125–150%. Em *Resolução máx.* dá para limitar mais (ex.: 1280 para Wi-Fi fraco).

---

## iPhone e iPad (sem App Store, sem conta de desenvolvedor)

Não existe jeito de instalar um app nativo de iOS sem conta de desenvolvedor que não expire em 7 dias (AltStore/SideStore) ou dependa de falhas de versões específicas (TrollStore). Por isso o cliente de iOS é um **app web instalável**, servido pelo próprio PC/Mac:

1. No app do computador, deixe **"Cliente web (iPhone/iPad)"** marcado. Aparece um **QR code** com o endereço (ex.: `http://192.168.0.10:27185/`).
2. No iPhone/iPad, aponte a câmera para o QR code (ou digite o endereço no Safari). Abre a **página instaladora**, que guia os passos:
   - **Baixar perfil** → *Ajustes > Perfil Baixado > Instalar* → *Ajustes > Geral > Sobre > Ajustes de Confiança de Certificados* → ativar **TabletMonitor CA**. É o certificado do seu computador: o Safari só decodifica vídeo H.264 (WebCodecs) em página segura (HTTPS). A página mostra "confiável ✓" quando der certo.
   - **Abrir TabletMonitor** → *Compartilhar > Adicionar à Tela de Início*. Daí em diante ele abre pelo ícone, em tela cheia, como um app.
3. Para deixar o aparelho dedicado: **Acesso Guiado** (Ajustes > Acessibilidade) trava o iPad no app; **Bloqueio Automático: Nunca** mantém a tela ligada.

| Modo | Requisito | Como funciona |
|---|---|---|
| **H.264** (padrão) | iOS/iPadOS 16.4+ e certificado instalado | WebCodecs decodifica por hardware; qualidade e atraso parecidos com o Wi-Fi do Android. |
| **JPEG** (compatível) | qualquer iOS, sem certificado | cada quadro é uma imagem; até 1280 px e 24 qps, gasta mais Wi-Fi. Botão "Modo compatível". |

O toque do iPhone/iPad funciona igual ao do tablet Android (multitoque no Windows; gestos de trackpad no Mac). O certificado é gerado no seu computador e só vale para ele; o do servidor é renovado a cada início com os IPs atuais (o perfil instalado no iPad continua valendo). Portas: **TCP 27185** (HTTP) e **27186** (HTTPS).

> Nos testes, o cliente web rodou no Chromium (mesmo WebCodecs do Safari) a 30 qps nos dois modos, com os toques chegando nas coordenadas certas. Ainda não foi testado num iPad de verdade.

---

## Mac

O app do Mac tem as mesmas funções do Windows (USB, Wi-Fi, cliente web, preparar o tablet, modo dedicado), com estas diferenças:

| | Windows | Mac |
|---|---|---|
| Captura | Desktop Duplication | `avfoundation` (permissão **Gravação de Tela**) |
| Encoder | NVENC / AMF / QuickSync / x264 | **VideoToolbox** (hardware de todo Mac) / x264 |
| Tela extra | driver de monitor virtual | **BetterDisplay** (grátis) ou plug HDMI "dummy" |
| Toque | toque nativo do Windows | o macOS não aceita toque externo: vira **mouse e trackpad** (permissão **Acessibilidade**) |
| Bluetooth | sim | não (só USB, Wi-Fi e navegador) |

**Gestos no Mac:** tocar = clique · dois toques = clique duplo · arrastar = arrastar/selecionar · **dois dedos arrastando = rolar** · **tocar com dois dedos = botão direito** (igual ao trackpad).

**Instalação:** o app não é assinado pela Apple (exige conta paga), então na primeira vez:

```bash
# depois de arrastar o TabletMonitor.app para Aplicativos:
xattr -cr /Applications/TabletMonitor.app && codesign --force --deep --sign - /Applications/TabletMonitor.app
```

O primeiro comando tira a "quarentena" do download; o segundo faz uma assinatura local, exigida pelos Macs com Apple Silicon. Depois, permita **Gravação de Tela**, **Acessibilidade** e **Rede local** quando o macOS pedir (o app mostra botões para abrir cada ajuste). No Apple Silicon, `brew install ffmpeg` dá um ffmpeg nativo mais rápido; sem ele o app baixa um que roda pelo Rosetta.

> Para iPad + Mac recentes, o **Sidecar** (nativo da Apple) também resolve; o TabletMonitor serve para o tablet Android, iPads antigos e iPhones.

---

## Passo 1 — Criar o "monitor" no Windows

Para o tablet **estender** a área de trabalho (e não só espelhar), o Windows precisa de um monitor a mais:

- **Monitor virtual (recomendado, grátis):** botão **"Instalar monitor virtual…"** no app (roda `winget install --id=VirtualDrivers.Virtual-Display-Driver -e`). Para adicionar a resolução sugerida, edite `C:\VirtualDisplayDriver\vdd_settings.xml`, dentro de `<resolutions>`:
  ```xml
  <resolution><width>1728</width><height>1080</height><refresh_rate>60</refresh_rate></resolution>
  ```
  e desative/reative o "Virtual Display Driver" no Gerenciador de Dispositivos.
- **Plug HDMI "dummy"** (~R$ 20) numa saída de vídeo livre.

Em *Configurações > Sistema > Tela*, arraste o monitor novo para o lado em que o tablet vai ficar.

## Passo 2 — Deixar o tablet dedicado

Há dois níveis; dá para usar só o primeiro ou os dois.

### Nível 1 — Modo dedicado (Device Owner), sem mexer na ROM

Usa o recurso oficial do Android para quiosques: o app vira a tela inicial, some a barra de status e a navegação, não há tela de bloqueio e não dá para abrir outros apps.

1. Tablet **sem nenhuma conta** (Google etc.) — o Android exige. O ideal é formatar e pular o login.
2. Depuração USB ativada: *Configurações > Sobre o tablet* → 7 toques em *Número da versão* → *Sistema > Opções do desenvolvedor > Depuração USB*.
3. Cabo no PC → no tablet, **Permitir** (marque *Sempre permitir*).
4. No app do Windows: **"Preparar tablet (modo dedicado)…"**.

### Nível 2 — ROM dedicada pelo TWRP (SM-T520)

O Android 10 no SM-T520 é uma LineageOS 17.1 não oficial, instalada pelo TWRP. O pacote `TabletMonitorROM-SM-T520.zip` **transforma essa ROM** num sistema que só faz isso:

- instala o TabletMonitor **como app de sistema** (não some num reset de fábrica e ganha permissão para ajustar o sistema sozinho);
- **remove a tela inicial** (Trebuchet) — o TabletMonitor passa a ser a única;
- remove os apps que não servem para um monitor (navegador, música, galeria, câmera, relógio, calculadora, atualizador, papéis de parede…) e, na variante `semgoogle`, também Play Store e Play Services — num tablet de 2 GB de RAM isso faz diferença;
- liga a depuração USB por padrão (modo USB funciona já no primeiro boot);
- ativa o **modo dedicado sozinho no primeiro boot** (pelo domínio `su` das builds userdebug da LineageOS — se a sua build não tiver, o modo dedicado é ativado pelo botão do Passo 2.1 na primeira conexão USB);
- troca a animação de inicialização pela do TabletMonitor.

**Nada é apagado:** tudo o que sai vai para `/system/.tabletmonitor-backup`, dentro da própria partição. O `TabletMonitorROM-restaurar.zip` devolve tudo — nos testes, o `/system` ficou idêntico byte a byte ao original.

**Instalação:**

1. Copie o .zip para o tablet (ou use `adb push`).
2. Desligue e entre no TWRP: **Home + Volume para cima + Power**.
3. *Backup* → marque **System** e **Boot** (recomendado, por segurança).
4. *Limpar > Formatar dados* (recomendado: começa sem contas, condição para o modo dedicado).
5. *Instalar* → escolha `TabletMonitorROM-SM-T520.zip` (ou a variante `semgoogle`).
6. *Reiniciar > Sistema*. O tablet liga direto no TabletMonitor.
7. Se for usar Wi-Fi: toque e segure 3 s → *Configurar Wi-Fi do tablet*.

Se o TWRP disser que `/system` está somente leitura: *Montar* → desmarque **"Montar a partição system como somente leitura"** e repita.

**Por que não uma ROM compilada do zero?** Compilar Android para o n2awifi exige a árvore do aparelho, o kernel e os blobs do Exynos 5420, ~300 GB de disco e horas de compilação — e o resultado seria a mesma LineageOS 17.1 que você já tem, com estas mudanças. O pacote do TWRP faz exatamente essas mudanças por cima dela, em segundos, e de forma reversível.

---

## Usar

**No Windows/Mac:** monitor a transmitir, encoder (Auto escolhe NVENC → AMF → QuickSync → software), quadros/s, bitrate, resolução máxima, perfil do Bluetooth e modo de toque. Mudar um ajuste reconecta o tablet em ~1 s. **"Iniciar com o Windows"** deixa tudo automático.

**Toque:** "Toque nativo do Windows" = tela sensível ao toque de verdade (rolar com o dedo, pinça, segurar = botão direito). Se algum programa não reagir bem, use "Mouse".

**No tablet:** com o PC desconectado, **toque e segure 3 s** na tela de espera: IP fixo do PC, orientação, Wi-Fi/Bluetooth ligados ou não, escurecer quando ocioso, configurar Wi-Fi, parear Bluetooth e **sair do modo dedicado**.

## Desfazer tudo

1. Tablet: toque e segure 3 s → **Sair do modo dedicado**.
2. Se instalou a ROM: TWRP → instale `TabletMonitorROM-restaurar.zip` → *Formatar dados*.
3. Se desativou apps pelo Windows (sem ROM): botão **"Reativar apps do tablet"**.

---

## Solução de problemas

| Sintoma | Causa provável / solução |
|---|---|
| "Procurando o PC…" no USB | Depuração USB desligada/não autorizada (o log do Windows avisa "não autorizado"). Troque o cabo — alguns só carregam. |
| "Procurando o PC…" no Wi-Fi | Firewall: permita o TabletMonitor em redes privadas (TCP 27183, UDP 27184). Redes de visitantes isolam aparelhos. Nas ROMs LineageOS para Exynos 5420 há relato de **não conectar em redes WPA2+WPA3 mistas** — se for o caso, use só WPA2 no roteador. |
| Bluetooth não conecta | PC e tablet pareados? Bluetooth ligado no PC e "Aceitar via Bluetooth" marcado? O tablet só tenta PCs pareados e a cada ~3 s. |
| "O PC não enviou vídeo" | Veja as linhas `ffmpeg:` no log. Teste o encoder `libx264`. |
| Imagem trava / cai para software | Baixe a *Resolução máx.* para 1280. |
| Imagem "pula" | Conexão lenta (o log mostra quadros descartados): baixe bitrate/qps, use 5 GHz ou USB. |
| Tela preta por um instante | Prompt do UAC / tela de bloqueio do Windows (não podem ser capturados). Reconecta sozinho. |
| `dpm set-device-owner` falha com "accounts" | Há conta no tablet. Remova todas ou formate. |
| Modo dedicado não ativou sozinho após a ROM | Sua build não tem `su`: use "Preparar tablet" no Windows uma vez (registro em `/data/local/tmp/tabletmonitor-owner.log`). |

Portas: **TCP 27183** (vídeo e toque), **UDP 27184** (descoberta). Bluetooth: serviço RFCOMM `5b8c9e3a-1f47-4d2e-a6b1-7c0e3d9f2a64`.

## Compilar do código-fonte

```bash
cd android && ./gradlew assembleRelease                 # APK (JDK 17+, Android SDK 34)
cp app/build/outputs/apk/release/app-release.apk ../desktop/TabletMonitor.Host/Assets/TabletMonitor.apk
cd ../desktop && dotnet publish TabletMonitor -c Release -o publish   # .exe (.NET 8; compila também no Linux)
./TabletMonitor.Mac/packaging/make-app.sh                 # .app do Mac (Apple Silicon e Intel)
cd ../rom && ./build.sh                                  # pacotes do TWRP (Python 3 + Pillow)
```

A chave de assinatura (`android/tabletmonitor.keystore`, senha `tabletmonitor`) está no projeto para as atualizações instalarem por cima, inclusive sobre o app de sistema da ROM.

**Testes sem hardware:** `desktop/CoreTest` sobe o servidor (TCP + web) com imagem sintética; `fake_tablet.py HOST PORTA saida.h264 [kB/s]` imita o tablet (o último argumento simula um link lento como o Bluetooth); `https://127.0.0.1:27186/app` num navegador testa o cliente web; `CoreTest gestos` testa a tradução toque → mouse do Mac. Para o pacote do TWRP, rode o `update-binary` com `TM_TEST_SYS=/caminho/de/um/system/de/teste`.

## Estrutura

```
android/…/tabletmonitor/
  MainActivity.kt   tela cheia, toque, tela de espera e configurações
  StreamClient.kt   conexão USB → Wi-Fi → Bluetooth, protocolo
  BluetoothLink.kt  RFCOMM com o PC pareado
  DecoderCaps.kt    maior vídeo que o decodificador aguenta
  VideoDecoder.kt   MediaCodec H.264 com ajustes de baixa latência por fabricante
  Discovery.kt      escuta os anúncios UDP do PC
  Kiosk.kt          Device Owner: lock task, tela inicial, sem barra/bloqueio
  SystemTweaks.kt   ajustes de sistema (com WRITE_SECURE_SETTINGS)
desktop/TabletMonitor.Core/   protocolo, FLV/MJPEG, servidor TCP, fila com descarte, servidor web + WebSocket,
                              certificados, cliente web (Web/app.html, Web/index.html) — multiplataforma
desktop/TabletMonitor.Host/   comum a Windows e Mac: configurações, adb/preparação do tablet, ffmpeg, gestos
desktop/TabletMonitor/        Windows: interface, monitores (DXGI), captura ddagrab, toque nativo, Bluetooth
desktop/TabletMonitor.Mac/    macOS (Avalonia): interface, telas (CoreGraphics), avfoundation, toque → mouse
rom/src/                      update-binary do TWRP, listas de remoção, permissões, init do modo dedicado
```
