; GBZEmu SGB bootstrap ROM (256 bytes)
;
; Original replacement firmware adapted from SameBoy's Expat-licensed SGB
; boot ROM. It forwards the cartridge header to the HLE SGB system over JOYP,
; displays the cartridge-provided header logo, and hands off with SGB register
; values. No Nintendo code or embedded logo data is included.

DEF rNR11 EQU $FF11
DEF rNR12 EQU $FF12
DEF rNR13 EQU $FF13
DEF rNR14 EQU $FF14
DEF rNR50 EQU $FF24
DEF rNR51 EQU $FF25
DEF rNR52 EQU $FF26
DEF rIF EQU $FF0F
DEF rLCDC EQU $FF40
DEF rBGP EQU $FF47
DEF rBANK EQU $FF50
DEF rIE EQU $FFFF
DEF VRAM_START EQU $8000
DEF TILEMAP EQU $9800
DEF COMMAND EQU $FF80

SECTION "BootCode", ROM0[$0000]
Start:
    ld sp, $FFFE

    ld hl, VRAM_START
    xor a
.clearVRAM
    ld [hl+], a
    bit 5, h
    jr z, .clearVRAM

    ld a, $80
    ldh [$FF26], a
    ldh [$FF11], a
    ld a, $F3
    ldh [$FF12], a
    ldh [$FF25], a
    ld a, $77
    ldh [$FF24], a

    xor a
    ldh [$FF47], a

    ld de, $0104
    ld hl, VRAM_START + $10
.loadLogo
    ld a, [de]
    ld b, a
    call DoubleBitsAndWriteRow
    call DoubleBitsAndWriteRow
    inc de
    ld a, e
    cp $34
    jr nz, .loadLogo

    ld de, TrademarkSymbol
    ld c, TrademarkSymbolEnd - TrademarkSymbol
.loadTrademark
    ld a, [de]
    inc de
    ld [hl+], a
    inc hl
    dec c
    jr nz, .loadTrademark

    ld a, $19
    ld [TILEMAP + 8 * 32 + 16], a
    ld hl, TILEMAP + 9 * 32 + 15
    ld c, 12
.tilemap
    dec a
    jr z, .tilemapDone
    ld [hl-], a
    dec c
    jr nz, .tilemap
    ld l, $0F
    jr .tilemap
.tilemapDone

    ld a, $91
    ldh [$FF40], a

    ld a, $F1
    ldh [COMMAND], a
    ld hl, $0104
    xor a
    ld c, a

.sendCommand
    xor a
    ldh [c], a
    ld a, $30
    ldh [c], a

    ldh a, [COMMAND]
    call SendByte
    push hl

    ld b, 14
    ld d, 0
.checksum
    call ReadHeaderByte
    add d
    ld d, a
    dec b
    jr nz, .checksum

    call SendByte
    pop hl
    ld b, 14
.sendHeader
    call ReadHeaderByte
    call SendByte
    dec b
    jr nz, .sendHeader

    ld a, $20
    ldh [c], a
    ld a, $30
    ldh [c], a

    ld e, 4
    ld a, 1
    ldh [rIE], a
    xor a
.wait
    ldh [rIF], a
    halt
    nop
    dec e
    jr nz, .wait
    ldh [rIE], a

    ldh a, [COMMAND]
    add 2
    ldh [COMMAND], a
    ld a, $58
    cp l
    jr nz, .sendCommand

    ld c, LOW(rNR13)
    ld a, $C1
    ldh [c], a
    inc c
    ld a, $07
    ldh [c], a

    ld a, $FC
    ldh [rBGP], a

IF DEF(SGB2)
    ld a, $FF
ELSE
    ld a, $01
ENDC
    or a
    ld hl, $C060
    jp BootGame

ReadHeaderByte:
    ld a, $4F
    cp l
    jr c, .zero
    ld a, [hl+]
    ret
.zero
    inc hl
    xor a
    ret

SendByte:
    ld e, a
    ld d, 8
.bit
    ld a, $10
    rr e
    jr c, .zeroBit
    add a
.zeroBit
    ldh [c], a
    ld a, $30
    ldh [c], a
    dec d
    ret z
    jr .bit

DoubleBitsAndWriteRow:
    ld a, 4
    ld c, 0
.bit
    sla b
    push af
    rl c
    pop af
    rl c
    dec a
    jr nz, .bit
    ld a, c
    ld [hl+], a
    inc hl
    ld [hl+], a
    inc hl
    ret

TrademarkSymbol:
    db $3C, $42, $B9, $A9, $B9, $A9, $42, $3C
TrademarkSymbolEnd:

SECTION "BootGame", ROM0[$00FE]
BootGame:
    ldh [rBANK], a
