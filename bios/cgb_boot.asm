; GBZEmu CGB boot ROM (2304 bytes)
;
; An original replacement for the stock CGB boot ROM, inspired by the
; restraint and motion of SameBoy's replacement: a purpose-rasterized, shaded
; 128x24 "GBZEmu" wordmark is revealed by a diagonal rainbow band, with the
; cartridge's own header logo ($0104-$0133) and a registered-trademark
; symbol rendered beneath it exactly like real firmware draws them. The
; classic two-note chime plays and a default DMG-compatibility palette is
; installed before handing off. This image contains no Nintendo code or
; logo data and performs no header logo/checksum lock-up, so any
; cartridge boots.
;
; The image layout matches the stock CGB boot ROM: code in $0000-$00FF
; and $0200-$08FF; $0100-$01FF is a hole where the cartridge header shows
; through. The emulator also reads a DMG title-checksum table from
; $06C7-$0716 (BOOT_ROM_CUSTOM_PALETTE_HASH_TABLE in Schemas.cs) to
; decide which DMG-only carts run in GBC compatibility mode, so that
; table lives at the same offsets here. The checksums are factual data
; (sums of cartridge title bytes, documented in Pan Docs).
;
; Hand-off contract (must match the emulator's skip-boot profile):
;   A=$11 F=$B0 BC=$0013 DE=$00D8 HL=$014D SP=$FFFE PC=$0100
;   LCDC=$91 SCY=$00 BGP=$FC OBP0=OBP1=$FF NR52=$F1 NR50=$77 NR51=$F3
;
; This image must also run cleanly when the emulator starts a DMG-only
; cartridge in DMG mode: CGB-only I/O writes (VBK, BCPS/BCPD, OCPS/OCPD)
; are ignored there, so nothing below depends on reading them back, and
; DMG mode is detected via KEY1 (reads $FF there) before attribute work.
;
; Build: rgbasm + rgblink (see build.sh).

SECTION "entry", ROM0[$0000]

Entry:
    ld sp, $FFFE
    jp Main

SECTION "tail", ROM0[$00EF]

; Hand-off register state; ends exactly at $0100 so execution falls into
; the cartridge entry point as the LDH unmaps the boot ROM.
Tail:
    ld bc, $0013
    ld de, $00D8
    ld hl, $014D
    ld a, $FF
    add a, $01              ; F = Z-HC = $B0
    ld a, $11
    ldh [$FF50], a

SECTION "main", ROM0[$0200]

Main:
    ; Clear VRAM bank 1 then bank 0. The VBK write is ignored when a
    ; DMG-only cart runs in DMG mode, so bank 0 is simply cleared twice.
    ld a, $01
    ldh [$FF4F], a
    call ClearVRAM
    xor a
    ldh [$FF4F], a
    call ClearVRAM

    ; Audio: power on, channel 1 50% duty, full volume with decay,
    ; both terminals enabled at max master volume.
    ld a, $80
    ldh [$FF26], a          ; NR52: APU on
    ldh [$FF11], a          ; NR11: duty 50%
    ld a, $F3
    ldh [$FF12], a          ; NR12: volume 15, decrease, period 3
    ldh [$FF25], a          ; NR51: routing
    ld a, $77
    ldh [$FF24], a          ; NR50: master volume max

    ; DMG-register palettes (used when the cart runs in DMG mode).
    ld a, $FC
    ldh [$FF47], a          ; BGP
    ld a, $FF
    ldh [$FF48], a          ; OBP0
    ldh [$FF49], a          ; OBP1

    ; CGB palette RAM. Palette 0 starts white so the wordmark can reveal
    ; cleanly; palettes 1-6 form its rainbow trail and palette 7 is the
    ; settled navy lockup. Palette 0 is restored to the compatibility
    ; grayscale ramp immediately before hand-off.
    ld a, $80
    ldh [$FF68], a          ; BCPS: index 0, auto-increment
    ld hl, BGPalettes
    ld b, 64
.bgPal
    ld a, [hl+]
    ldh [$FF69], a          ; BCPD
    dec b
    jr nz, .bgPal

    ; All OBJ palettes get the compatibility grayscale ramp.
    ld a, $80
    ldh [$FF6A], a          ; OCPS: index 0, auto-increment
    ld b, 8
.objPals
    ld hl, GrayscalePalette
    ld d, 8
.objPalByte
    ld a, [hl+]
    ldh [$FF6B], a          ; OCPD
    dec d
    jr nz, .objPalByte
    dec b
    jr nz, .objPals

    ; Decompress the shaded 128x24 wordmark into tiles 1-48. The compact
    ; stream is PackBits-style RLE; B counts tiles and C bytes per tile.
    ld hl, HeroLogoRLE
    ld de, $8010
    ld b, 48
    ld c, 16
    call DecodeRLE

    ; Decode the cartridge header logo into tiles 49-72. Each nibble is
    ; bit-doubled into two rows and both bitplanes so it uses color 3.
    ld de, $0104
    ld hl, $8310
.decodeHeader
    ld a, [de]
    call ExpandNibble
    call ExpandNibble.next
    inc de
    ld a, e
    cp $34
    jr nz, .decodeHeader

    ; Registered-trademark tile (73), both bitplanes set (color 3).
    ld de, TrademarkTile
    ld b, $08
.copyTrademark
    ld a, [de]
    inc de
    ld [hl+], a
    ld [hl+], a
    dec b
    jr nz, .copyTrademark

    ; Tilemap: the wide wordmark occupies rows 5-7, with the cartridge
    ; header logo on rows 10-11 and its trademark in the superscript cell.
    ld hl, $9800 + 5 * 32 + 2
    ld a, $01
    ld b, 16
    call MapCells
    ld l, LOW($9800 + 6 * 32 + 2)
    ld b, 16
    call MapCells
    ld l, LOW($9800 + 7 * 32 + 2)
    ld b, 16
    call MapCells
    ld hl, $9800 + 10 * 32 + 4
    ld b, 12
    call MapCells
    ld l, LOW($9800 + 11 * 32 + 4)
    ld b, 12
    call MapCells
    ld l, LOW($9800 + 10 * 32 + 16)
    ld [hl], a

    ; Bank-1 attributes: the hero starts on all-white palette 0; the header
    ; logo and trademark use the settled palette 7. KEY1 reads $FF in DMG
    ; mode, where VBK is ignored and these writes would hit the tilemap.
    ldh a, [$FF4D]
    inc a
    jr z, .noAttributes
    ld a, $01
    ldh [$FF4F], a          ; VBK = 1
    ld hl, $9800 + 10 * 32 + 4
    ld b, 13                ; 12 logo cells plus the trademark at column 16
    ld a, $07
    call FillCells
    ld l, LOW($9800 + 11 * 32 + 4)
    ld b, 12
    call FillCells
    xor a
    ldh [$FF4F], a          ; VBK = 0
.noAttributes

    ; Switch the LCD on with the cartridge mark centered and the hero hidden.
    ld a, $91
    ldh [$FF40], a          ; LCDC: LCD on, BG on, unsigned tile data

    ; Reveal the wordmark with a fine 8-pixel rainbow band. Every two
    ; frames, colored cells advance one palette and the next white cell
    ; enters the band; the three rows are offset for a subtle diagonal.
    ; Palette 7 is terminal, leaving the whole mark in deep navy.
    ld b, 25
.revealFrame
    call WaitFrame
    call WaitFrame
    ldh a, [$FF4D]
    inc a
    jr z, .nextRevealFrame
    ld a, $01
    ldh [$FF4F], a          ; VBK = 1
    ld hl, $9800 + 5 * 32 + 2
    call AdvanceRevealRow
    ld a, b
    cp 25
    jr z, .skipMiddleRow
    ld hl, $9800 + 6 * 32 + 2
    call AdvanceRevealRow
.skipMiddleRow
    ld a, b
    cp 24
    jr nc, .skipBottomRow
    ld hl, $9800 + 7 * 32 + 2
    call AdvanceRevealRow
.skipBottomRow
    xor a
    ldh [$FF4F], a          ; VBK = 0
.nextRevealFrame
    dec b
    jr nz, .revealFrame

    ; Two-note chime on channel 1.
    ld a, $83
    ldh [$FF13], a
    ld a, $87
    ldh [$FF14], a          ; trigger ~1048 Hz
    ld c, 8
.noteGap
    call WaitFrame
    dec c
    jr nz, .noteGap
    ld a, $C1
    ldh [$FF13], a
    ld a, $87
    ldh [$FF14], a          ; trigger ~2080 Hz

    ; Hold the logo, then hand off.
    ld c, 36
.hold
    call WaitFrame
    dec c
    jr nz, .hold

    ; Restore the grayscale compatibility palette and clear attributes
    ; during vblank, so DMG games inherit the expected clean palette/map.
    ldh a, [$FF4D]
    inc a
    jr z, .noAttrClear
    ld a, $80
    ldh [$FF68], a
    ld hl, GrayscalePalette
    ld b, 8
.restoreGray
    ld a, [hl+]
    ldh [$FF69], a
    dec b
    jr nz, .restoreGray
    ld a, $01
    ldh [$FF4F], a          ; VBK = 1
    ld hl, $9800 + 5 * 32 + 2
    ld b, 16
    xor a
    call FillCells
    ld l, LOW($9800 + 6 * 32 + 2)
    ld b, 16
    call FillCells
    ld l, LOW($9800 + 7 * 32 + 2)
    ld b, 16
    call FillCells
    ld hl, $9800 + 10 * 32 + 4
    ld b, 13
    call FillCells
    ld l, LOW($9800 + 11 * 32 + 4)
    ld b, 12
    call FillCells
    xor a
    ldh [$FF4F], a          ; VBK = 0
.noAttrClear

    jp Tail

ClearVRAM:
    xor a
    ld hl, $9FFF
.loop
    ld [hl-], a
    bit 7, h
    jr nz, .loop
    ret

; Advances the active rainbow band across one 16-cell wordmark row.
; Settled palette-7 cells are skipped; advancing a white cell starts the
; new leading edge and ends this row for the current animation step.
AdvanceRevealRow:
    ld c, 16
.cell
    ld a, [hl]
    cp $07
    jr z, .next
    inc [hl]
    or a
    ret z
.next
    inc hl
    dec c
    jr nz, .cell
    ret

; Writes A to B consecutive cells starting at HL. A is preserved.
FillCells:
.cell
    ld [hl+], a
    dec b
    jr nz, .cell
    ret

; Writes B consecutive tile numbers starting at A to the map row at HL.
MapCells:
.cell
    ld [hl+], a
    inc a
    dec b
    jr nz, .cell
    ret

; Returns at the start of the next vblank; the leading loop ensures each
; call waits for a fresh frame even when called from within vblank.
WaitFrame:
.leaveVBlank
    ldh a, [$FF44]
    cp $90
    jr z, .leaveVBlank
.waitVBlank
    ldh a, [$FF44]
    cp $90
    jr nz, .waitVBlank
    ret

; Bit-doubles the next nibble of C into A and writes it as two tile rows
; on both bitplanes (color 3). Entry point ExpandNibble primes C from A;
; calling .next afterwards processes the remaining nibble.
ExpandNibble:
    ld c, a
.next
    ld b, $04
.bit
    push bc
    rl c
    rla
    pop bc
    rl c
    rla
    dec b
    jr nz, .bit
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ret

; Expands the PackBits-style wordmark stream from HL into tiles at DE.
; Literal controls encode 1-128 following bytes; controls with bit 7 set
; encode 2-129 repetitions. B/C track 48 tiles of 16 output bytes.
DecodeRLE:
.block
    ld a, [hl+]
    bit 7, a
    jr nz, .repeat
    inc a
    ldh [$FF80], a
.literalByte
    ld a, [hl+]
    ld [de], a
    inc de
    dec c
    jr nz, .literalCount
    ld c, 16
    dec b
.literalCount
    ldh a, [$FF80]
    dec a
    ldh [$FF80], a
    jr nz, .literalByte
    ld a, b
    or a
    jr nz, .block
    ret
.repeat
    and a, $7F
    add a, $02
    ldh [$FF80], a
    ld a, [hl+]
    ldh [$FF81], a
.repeatByte
    ldh a, [$FF81]
    ld [de], a
    inc de
    dec c
    jr nz, .repeatCount
    ld c, 16
    dec b
.repeatCount
    ldh a, [$FF80]
    dec a
    ldh [$FF80], a
    jr nz, .repeatByte
    ld a, b
    or a
    jr nz, .block
    ret

; RGB555 background palettes. The wordmark's three ink levels use a light
; edge, midtone, and solid fill; the band progresses from cyan through
; warm hues to violet before settling into the palette-7 navy.
BGPalettes:
    dw $7FFF, $7FFF, $7FFF, $7FFF ; 0: invisible white lead-in
    dw $7FFF, $7F97, $7B2E, $7AC4 ; 1: cyan
    dw $7FFF, $6BB8, $4F6F, $3325 ; 2: green
    dw $7FFF, $5F9F, $373F, $0ADF ; 3: gold
    dw $7FFF, $5F3F, $325F, $055F ; 4: orange
    dw $7FFF, $6B1E, $51FD, $34BC ; 5: pink
    dw $7FFF, $771C, $6E18, $64F4 ; 6: violet
    dw $7FFF, $6B17, $520E, $38C4 ; 7: settled navy

GrayscalePalette:
    dw $7FFF, $56B5, $294A, $0000

; Shaded 128x24 GBZEmu wordmark, stored in tile order with compact RLE.
HeroLogoRLE:
    db $86, $00, $07, $01, $00, $01, $00, $03, $01, $07, $03, $82, $00, $0B, $18, $07
    db $6F, $1F, $BF, $7F, $FF, $FF, $F3, $FC, $E8, $F0, $82, $00, $0B, $18, $E0, $F4
    db $F8, $FA, $FC, $FD, $FE, $DE, $3F, $1F, $0F, $82, $00, $0B, $07, $00, $05, $03
    db $03, $07, $0B, $07, $8B, $07, $8F, $07, $82, $00, $01, $FF, $00, $84, $FF, $03
    db $C0, $FF, $A1, $C0, $82, $00, $0B, $C0, $00, $30, $C0, $E8, $F0, $F5, $F8, $F5
    db $F8, $FC, $F8, $82, $00, $0A, $FF, $00, $FF, $7F, $7F, $FF, $7F, $FF, $80, $7F
    db $01, $83, $00, $0B, $FF, $00, $FE, $FF, $FE, $FF, $FE, $FF, $FD, $FE, $FA, $FC
    db $82, $00, $0B, $1F, $00, $97, $0F, $8F, $1F, $2F, $1F, $2F, $1F, $3E, $1F, $82
    db $00, $01, $FF, $00, $84, $FF, $02, $00, $FF, $80, $83, $00, $0A, $F0, $00, $F0
    db $E0, $F0, $E0, $D0, $E0, $30, $C0, $07, $8D, $00, $01, $F1, $0E, $8C, $00, $01
    db $98, $07, $8C, $00, $00, $C7, $8D, $00, $00, $E1, $8D, $00, $0A, $F0, $00, $0B
    db $07, $0F, $07, $07, $0F, $07, $0F, $07, $83, $0F, $36, $07, $0F, $D0, $E0, $A0
    db $C0, $83, $C0, $C1, $83, $45, $83, $47, $83, $44, $83, $C7, $80, $10, $0F, $00
    db $00, $FF, $00, $FE, $FF, $FE, $FF, $FF, $FE, $3D, $FE, $FD, $1E, $87, $0F, $17
    db $0F, $1F, $0F, $1F, $0F, $0F, $1F, $2F, $1F, $2F, $1F, $3E, $1F, $81, $C0, $BF
    db $C1, $C7, $83, $FF, $30, $83, $FF, $83, $01, $82, $01, $F4, $F8, $F4, $F8, $E8
    db $F0, $E0, $C0, $D0, $E0, $E8, $F0, $F0, $F8, $F1, $F8, $02, $01, $05, $03, $0B
    db $07, $17, $0F, $2F, $1F, $5F, $3F, $BE, $7F, $7D, $FE, $F4, $F8, $E8, $F0, $D0
    db $E0, $A0, $C0, $40, $80, $80, $83, $00, $13, $1E, $3F, $5E, $3F, $5F, $3F, $7F
    db $3F, $3F, $7F, $BC, $7F, $BE, $7C, $FA, $7C, $00, $00, $FF, $00, $84, $FF, $01
    db $00, $FF, $82, $00, $10, $0B, $07, $CB, $07, $4F, $87, $C7, $8F, $47, $8F, $97
    db $0F, $1F, $0F, $0F, $1F, $9F, $81, $FF, $54, $C7, $FF, $87, $CF, $87, $CF, $C7
    db $8F, $57, $8F, $1F, $8F, $6F, $9F, $FF, $FF, $E3, $FF, $D7, $E3, $A7, $C3, $83
    db $C7, $C3, $87, $4B, $87, $A3, $C7, $CB, $E7, $CF, $E7, $C7, $EF, $C7, $EF, $F7
    db $CF, $F7, $CF, $BF, $CF, $A0, $C1, $E2, $C1, $A3, $C1, $81, $C3, $81, $C3, $C5
    db $83, $47, $83, $43, $87, $E0, $F0, $E0, $F0, $E0, $F0, $F0, $E0, $F0, $E0, $F0
    db $E0, $D0, $E0, $C0, $E0, $07, $0F, $0F, $07, $0B, $07, $05, $03, $03, $85, $00
    db $03, $A0, $C0, $DF, $E0, $82, $FF, $03, $7F, $FF, $E0, $1F, $82, $00, $0A, $1C
    db $3E, $3E, $FC, $FE, $FC, $FA, $FC, $E8, $F0, $E0, $83, $00, $0A, $1E, $3F, $5E
    db $3F, $5F, $3F, $7F, $3F, $3F, $7F, $7F, $83, $00, $03, $02, $01, $FD, $03, $82
    db $FF, $02, $FE, $FF, $FF, $83, $00, $0A, $F2, $F9, $FD, $F3, $E3, $F7, $DB, $E7
    db $4B, $87, $0F, $83, $00, $03, $FA, $FC, $FB, $FC, $85, $FF, $85, $00, $08, $FF
    db $00, $FB, $FC, $FB, $FC, $F8, $FD, $FD, $83, $00, $04, $7A, $FC, $7B, $FC, $7F
    db $84, $FF, $85, $00, $01, $FF, $00, $85, $FF, $83, $00, $0A, $0F, $1F, $AF, $1F
    db $3E, $9F, $9E, $3F, $9F, $3E, $BF, $83, $00, $0A, $8F, $1F, $AF, $1F, $AE, $1F
    db $3E, $1F, $1E, $3F, $3F, $83, $00, $0A, $0F, $87, $87, $0F, $97, $0F, $97, $0F
    db $1F, $0F, $1F, $83, $00, $0B, $AF, $DF, $8F, $DF, $CF, $9F, $5F, $8F, $57, $8F
    db $8C, $03, $82, $00, $04, $0B, $87, $37, $8F, $9F, $81, $FF, $03, $E7, $FF, $7F
    db $80, $82, $00, $0A, $C0, $E0, $E0, $C0, $A0, $C0, $80, $C0, $80, $C0, $C0, $83
    db $00

; 8x8 registered-trademark symbol (circled R).
TrademarkTile:
    db $3C, $42, $B9, $A9, $B1, $A9, $42, $3C

; DMG title-checksum table at the stock offsets ($06C7-$0716); the
; emulator reads it to grant known DMG carts a compatibility palette.
SECTION "hashtable", ROM0[$06C7]
    db $00, $88, $16, $36, $D1, $DB, $F2, $3C, $8C, $92, $3D, $5C, $58, $C9, $3E, $70
    db $1D, $59, $69, $19, $35, $A8, $14, $AA, $75, $95, $99, $34, $6F, $15, $FF, $97
    db $4B, $90, $17, $10, $39, $F7, $F6, $A2, $49, $4E, $43, $68, $E0, $8B, $F0, $CE
    db $0C, $29, $E8, $B7, $86, $9A, $52, $01, $9D, $71, $9C, $BD, $5D, $6D, $67, $3F
    db $6B, $B3, $46, $28, $A5, $C6, $D3, $27, $61, $18, $66, $6A, $BF, $0D, $F4, $42

; Force the linked image to the full 2304-byte CGB boot ROM size.
SECTION "endpad", ROM0[$08FF]
    db $00
