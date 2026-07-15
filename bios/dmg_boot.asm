; GBZEmu DMG boot ROM (256 bytes)
;
; An original replacement for the stock DMG boot ROM, SameBoy-style: a
; large pixel-doubled "GBZEmu" wordmark is the hero graphic, with the
; cartridge's own header logo ($0104-$0133) and a registered-trademark
; symbol rendered beneath it exactly like real firmware draws them. The
; whole lockup scrolls down the screen and the classic two-note chime
; plays before handing off. This image contains no Nintendo code or logo
; data and performs no header logo/checksum lock-up, so any cartridge
; boots.
;
; To fit 256 bytes this image relies on two GBZEmu reset guarantees (it
; is embedded in and specific to this emulator): VRAM starts zeroed, and
; the APU reset profile (NR52=$F1, NR50=$77, NR51=$F3, NR12=$F3) is
; already applied, so it neither clears VRAM nor initializes audio
; registers before playing the chime.
;
; Hand-off contract (must match the emulator's skip-boot profile):
;   A=$01 F=$B0 BC=$0013 DE=$00D8 HL=$014D SP=$FFFE PC=$0100
;   LCDC=$91 SCY=$00 BGP=$FC OBP0=OBP1=$FF
;
; Byte $00FD is the scroll speed in pixels per frame (default 1). The
; emulator's short-boot mode patches it to 3, which also divides the logo
; hold. The final two bytes must be LDH [$FF50],A so the boot ROM unmaps
; itself with PC landing exactly on $0100.
;
; Build: rgbasm + rgblink (see build.sh).

DEF SPEED_ADDR EQU $00FD

SECTION "main", ROM0[$0000]

Entry:
    ld sp, $FFFE

    ; DMG-register palettes.
    ld a, $FC
    ldh [$FF47], a          ; BGP: colors 1-3 darkest, color 0 lightest
    ld a, $FF
    ldh [$FF48], a          ; OBP0
    ldh [$FF49], a          ; OBP1

    ; Decode the GBZEmu wordmark (tiles 1-24), then the cartridge header
    ; logo (tiles 25-48). Both use the header-logo nibble format: each
    ; nibble is bit-doubled and written as two rows on bitplane 0 only,
    ; so 48x8 source pixels render 96x16 in color 1.
    ld de, HeroLogo
    ld hl, $8010
.decode
    ld a, [de]
    call ExpandNibble
    call ExpandNibble.next
    inc de
    ld a, e
    cp $34                  ; past the header logo: done
    jr z, .decoded
    cp LOW(HeroLogoEnd)     ; past the wordmark: switch to the header
    jr nz, .decode
    ld de, $0104
    jr .decode
.decoded

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

    ; Start with the lockup above the viewport and switch the LCD on. 99
    ; is divisible by both supported scroll speeds (1 and 3), so the
    ; scroll loop lands exactly on 0 without clamping.
    ld a, $63
    ldh [$FF42], a          ; SCY = 99
    ld a, $91
    ldh [$FF40], a          ; LCDC: LCD on, BG on, unsigned tile data

    ; Scroll down SPEED pixels per frame until SCY reaches 0.
.scroll
    call WaitFrame
    ld a, [SPEED_ADDR]
    ld b, a
    ldh a, [$FF42]
    sub b
    ldh [$FF42], a
    jr nz, .scroll

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

    ; Hold the logo for 75 frames divided by the scroll speed (75 divides
    ; exactly by both speeds, so the countdown lands on zero).
    ld c, 75
.hold
    call WaitFrame
    ld a, [SPEED_ADDR]
    ld b, a
    ld a, c
    sub b
    ld c, a
    jr nz, .hold

    ; Hand-off register state, then unmap via the tail at $00FE.
    ld bc, $0013
    ld de, $00D8
    ld hl, $014D
    ld a, $FF
    add a, $01              ; F = Z-HC = $B0
    ld a, $01
    jr Handoff

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

; Writes twelve consecutive tile numbers starting at A to the map row at HL.
MapRow:
    ld b, $0C
.cell
    ld [hl+], a
    inc a
    dec b
    jr nz, .cell
    ret

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

; The combined decode loop tells its two passes apart by E alone, so the
; wordmark bytes must live entirely above the header-logo range.
ASSERT LOW(HeroLogo) > $34
ASSERT LOW(HeroLogoEnd) > LOW(HeroLogo)

SECTION "speed", ROM0[SPEED_ADDR]
    db $01                  ; pixels per frame; short boot patches this to 3

SECTION "handoff", ROM0[$00FE]
Handoff:
    ldh [$FF50], a          ; unmap boot ROM; execution falls into $0100
