; GBZEmu CGB boot ROM (2304 bytes)
;
; An original replacement for the stock CGB boot ROM, SameBoy-style: a
; large pixel-doubled "GBZEmu" wordmark is the hero graphic — black on
; white with a rainbow color sweep washing through it — with the
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

    ; CGB palette RAM. BG palette 0 is a white-to-black grayscale ramp:
    ; it is both the boot background and the palette DMG carts keep in
    ; compatibility mode (matching real-hardware defaults for unlisted
    ; carts). Palettes 1-6 belong to one pair of wordmark columns each
    ; (black until the color sweep animates them); palette 7 renders the
    ; header logo and trademark in black.
    ld a, $80
    ldh [$FF68], a          ; BCPS: index 0, auto-increment
    ld hl, BGPalettes
    ld b, 64
.bgPal
    ld a, [hl+]
    ldh [$FF69], a          ; BCPD
    dec b
    jr nz, .bgPal

    ; All OBJ palettes get the grayscale ramp (palette 0 of the BG table).
    ld a, $80
    ldh [$FF6A], a          ; OCPS: index 0, auto-increment
    ld b, 8
.objPals
    ld hl, BGPalettes
    ld d, 8
.objPalByte
    ld a, [hl+]
    ldh [$FF6B], a          ; OCPD
    dec d
    jr nz, .objPalByte
    dec b
    jr nz, .objPals

    ; Decode the GBZEmu wordmark (tiles 1-24), then the cartridge header
    ; logo (tiles 25-48). Both use the header-logo nibble format: each
    ; nibble is bit-doubled and written as two rows on bitplane 0 only,
    ; so 48x8 source pixels render 96x16 in color 1.
    ld de, HeroLogo
    ld hl, $8010
.decodeHero
    ld a, [de]
    call ExpandNibble
    call ExpandNibble.next
    inc de
    ld a, e
    cp LOW(HeroLogoEnd)
    jr nz, .decodeHero
    ld de, $0104
.decodeHeader
    ld a, [de]
    call ExpandNibble
    call ExpandNibble.next
    inc de
    ld a, e
    cp $34
    jr nz, .decodeHeader

    ; Registered-trademark tile (49), both bitplanes set (color 3).
    ld de, TrademarkTile
    ld b, $08
.copyTrademark
    ld a, [de]
    inc de
    ld [hl+], a
    ld [hl+], a
    dec b
    jr nz, .copyTrademark

    ; Tilemap: wordmark on rows 6-7, header logo on rows 10-11 (columns
    ; 4-15 each), trademark superscript at row 10, column 16. The tile
    ; number in A runs on from row to row.
    ld hl, $9800 + 6 * 32 + 4
    ld a, $01
    call MapRow
    ld l, LOW($9800 + 7 * 32 + 4)
    call MapRow
    ld hl, $9800 + 10 * 32 + 4
    call MapRow
    ld l, LOW($9800 + 11 * 32 + 4)
    call MapRow
    ld l, LOW($9800 + 10 * 32 + 16)
    ld [hl], a

    ; Bank-1 attributes: wordmark column pairs get palettes 1-6 for the
    ; sweep; the header logo rows and trademark cell get palette 7. KEY1
    ; reads $FF in DMG mode, where VBK and the attribute map do not exist
    ; and the writes below would corrupt the bank-0 tilemap instead.
    ldh a, [$FF4D]
    inc a
    jr z, .noAttributes
    ld a, $01
    ldh [$FF4F], a          ; VBK = 1
    ld hl, $9800 + 6 * 32 + 4
    call WriteSweepAttributes
    ld l, LOW($9800 + 7 * 32 + 4)
    call WriteSweepAttributes
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

    ; The CGB boot does not scroll: switch the LCD on with the lockup
    ; already centered, black on white, like the original firmware.
    ld a, $91
    ldh [$FF40], a          ; LCDC: LCD on, BG on, unsigned tile data

    ; Color sweep: a rainbow band washes through the wordmark left to
    ; right, then it settles back to black. Each frame updates color 1
    ; (the logo pixel color) of the six column-pair palettes; the writes
    ; happen right after vblank starts. In DMG mode the palette writes
    ; are ignored and the logo simply holds.
    ;
    ; For frame counter B, column pair C shows hue (B/2 - 4 - 3*C);
    ; values outside the hue table render black, which gives the lead-in,
    ; the band, and the settle without extra phases.
    ld b, $00
.sweepFrame
    push bc
    call WaitFrame
    pop bc
    push bc
    ld a, b
    srl a
    sub a, $04
    ld d, a                 ; d = base hue step (may wrap negative)
    ld c, $00               ; c = column-pair index
.sweepPair
    ; BCPS: palette (C+1), color 1, auto-increment
    ld a, c
    inc a
    add a, a
    add a, a
    add a, a
    add a, $02
    or a, $80
    ldh [$FF68], a
    ; h = d - 3*C; wrapped negatives fail the range check and render black
    ld a, c
    add a, c
    add a, c
    ld e, a
    ld a, d
    sub a, e
    cp $08
    jr c, .hueColor
    xor a
    ldh [$FF69], a
    ldh [$FF69], a
    jr .nextPair
.hueColor
    add a, a
    ld hl, HueTable
    add a, l
    ld l, a
    jr nc, .hueNoCarry
    inc h
.hueNoCarry
    ld a, [hl+]
    ldh [$FF69], a
    ld a, [hl]
    ldh [$FF69], a
.nextPair
    inc c
    ld a, c
    cp $06
    jr nz, .sweepPair
    pop bc
    inc b
    ld a, b
    cp 62                   ; (4 lead-in + 8 hues + 15 pair offset) * 2
    jr nz, .sweepFrame

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
    ld c, 45
.hold
    call WaitFrame
    dec c
    jr nz, .hold

    ; Clear the attributes (safe: the hold loop just returned at the
    ; start of vblank) so compatibility-mode games start with a clean
    ; attribute map. The tile indices remain, like stock firmware
    ; leftovers, dimming to the ramp's gray at hand-off.
    ldh a, [$FF4D]
    inc a
    jr z, .noAttrClear
    ld a, $01
    ldh [$FF4F], a          ; VBK = 1
    ld hl, $9800 + 6 * 32 + 4
    ld b, 12
    xor a
    call FillCells
    ld l, LOW($9800 + 7 * 32 + 4)
    ld b, 12
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

; Writes palette indices 1-6 across the twelve wordmark cells starting at
; HL, two columns per palette.
WriteSweepAttributes:
    ld a, $01
.pair
    ld [hl+], a
    ld [hl+], a
    inc a
    cp $07
    jr nz, .pair
    ret

; Writes A to B consecutive cells starting at HL. A is preserved.
FillCells:
.cell
    ld [hl+], a
    dec b
    jr nz, .cell
    ret

; Writes twelve consecutive tile numbers starting at A to the map row at HL.
MapRow:
    ld b, $0C
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
; on bitplane 0. Entry point ExpandNibble primes C from A first; calling
; .next afterwards processes the remaining nibble.
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
    inc hl
    ld [hl+], a
    inc hl
    ret

; RGB555 background palettes: palette 0 is the white-to-black grayscale
; ramp; palettes 1-6 belong to one pair of wordmark columns each (the
; sweep animates their color 1); palette 7 renders the header logo
; (color 1) and trademark (color 3) in black on white.
BGPalettes:
    dw $7FFF, $56B5, $294A, $0000 ; 0: grayscale ramp (kept by DMG carts)
    dw $7FFF, $0000, $0000, $0000 ; 1: wordmark columns 1-2
    dw $7FFF, $0000, $0000, $0000 ; 2: wordmark columns 3-4
    dw $7FFF, $0000, $0000, $0000 ; 3: wordmark columns 5-6
    dw $7FFF, $0000, $0000, $0000 ; 4: wordmark columns 7-8
    dw $7FFF, $0000, $0000, $0000 ; 5: wordmark columns 9-10
    dw $7FFF, $0000, $0000, $0000 ; 6: wordmark columns 11-12
    dw $7FFF, $0000, $294A, $0000 ; 7: header logo and trademark

; The color-sweep band, warm to cool (RGB555).
HueTable:
    dw $109F ; red
    dw $025F ; orange
    dw $039E ; yellow
    dw $2342 ; green
    dw $6720 ; teal
    dw $7D84 ; blue
    dw $7098 ; purple
    dw $609C ; magenta

; "GBZEmu" as a 48x8 wordmark in the header-logo nibble format
; (generated from the GBZEmu font; see bios/README.md).
HeroLogo:
    db $37, $EC, $EE, $00, $FF, $CF, $CE, $6C, $FF, $01, $EE, $C8, $FF, $CF, $EE, $0C
    db $00, $EF, $00, $CE, $00, $CC, $00, $66, $CC, $73, $E6, $EC, $CC, $FF, $66, $EC
    db $36, $FF, $00, $EE, $FC, $FF, $C0, $EE, $DD, $DD, $66, $66, $CC, $F7, $66, $EC
HeroLogoEnd:

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
