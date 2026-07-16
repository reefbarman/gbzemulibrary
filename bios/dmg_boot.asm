; GBZEmu DMG boot ROM (256 bytes)
;
; An original replacement for the stock DMG boot ROM: a compact italic
; "GBZEmu" wordmark expands into a two-tone shaded hero graphic, with the
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
; Byte $00FD is the scroll duration in frames (default 60). The emulator's
; short-boot mode patches it to 20. The final two bytes must be LDH
; [$FF50],A so the boot ROM unmaps
; itself with PC landing exactly on $0100.
;
; Build: rgbasm + rgblink (see build.sh).

DEF SPEED_ADDR EQU $00FD

SECTION "main", ROM0[$0000]

Entry:
    ld sp, $FFFE

    ; Use two ink shades against the DMG background. Only the GBZEmu mark
    ; uses the middle shade; the cartridge logo remains solid black.
    ld a, $E8
    ldh [$FF47], a          ; BGP: middle-shade hero and solid-black logo
    ld a, $FF
    ldh [$FF48], a          ; OBP0
    ldh [$FF49], a          ; OBP1

    ; Decode the GBZEmu wordmark (tiles 1-24), then the cartridge header
    ; logo (tiles 25-48). Both use the header-logo nibble format, but the
    ; source pointer's high byte selects shaded hero or solid header output.
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

    ; Tilemap: wordmark on rows 6-7, header logo on rows 9-10 (columns
    ; 4-15 each), trademark superscript at row 10, column 16. The tile
    ; number in A runs on from row to row.
    ld hl, $9800 + 6 * 32 + 4
    ld a, $01
    call MapRow
    ld l, LOW($9800 + 7 * 32 + 4)
    call MapRow
    ld hl, $9800 + 9 * 32 + 4
    call MapRow
    ld l, LOW($9800 + 10 * 32 + 4)
    call MapRow
    ld l, LOW($9800 + 9 * 32 + 16)
    ld [hl], a

    ; Start with the lockup above the viewport and switch the LCD on. The
    ; patchable duration is also the initial scroll distance in pixels.
    ld a, [SPEED_ADDR]
    ld b, a
    ldh [$FF42], a
    ld a, $91
    ldh [$FF40], a          ; LCDC: LCD on, BG on, unsigned tile data

    ; Scroll down one pixel per frame until SCY reaches 0.
.scroll
    call WaitFrame
    ldh a, [$FF42]
    dec a
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

    ; Hand-off register state, then unmap via the tail at $00FE.
    ld bc, $0013
    ld de, $00D8
    ld hl, $014D
    ; Restore the post-boot palette. Adding four to $FC also produces zero
    ; with F = Z-HC = $B0, setting the required hand-off flags compactly.
    ld a, $FC
    ldh [$FF47], a
    add a, $04
    ld a, $01
    jr Handoff

; Returns at the next vblank edge. HL is scratch here: every caller has
; completed tilemap setup, and the hand-off reloads HL explicitly.
WaitFrame:
    ld hl, $FF0F
    res 0, [hl]
.wait
    bit 0, [hl]
    jr z, .wait
    ret

; Bit-doubles the next nibble of C into A. The hero's upper tile row uses
; the middle shade and its lower row uses the darkest shade, producing one
; coherent vertical treatment without per-row stripes or tile-edge shifts.
; Cartridge data has D=1 and always renders solid, matching the original
; BIOS. ExpandNibble primes C; calling .next processes the next nibble.
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
    ld b, a
    ld a, d
    or a
    jr nz, .restoreSolid
    ld a, e
    cp LOW(HeroLogo + 24)
    ld a, b
    jr nc, .solid
    inc hl
    ld [hl+], a
    inc hl
    ld [hl+], a
    ret
.restoreSolid
    ld a, b
.solid
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
    ld [hl+], a
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

; Italic "GBZEmu" as a purpose-rasterized 48x8 wordmark in the compact
; header-logo nibble format. It expands to a crisp 96x16 lockup.
HeroLogo:
    db $13, $76, $FF, $07, $1B, $33, $FF, $BF, $3B, $81, $FF, $EC, $77, $6F, $EE, $0E
    db $00, $FD, $00, $FB, $00, $76, $00, $66, $67, $30, $7F, $E0, $B7, $70, $3F, $F0
    db $BF, $70, $8E, $E0, $CF, $F0, $1D, $D0, $9B, $B0, $BB, $30, $66, $70, $CC, $C0
HeroLogoEnd:

; 8x8 registered-trademark symbol (circled R).
TrademarkTile:
    db $3C, $42, $B9, $A9, $B1, $A9, $42, $3C

; The combined decode loop tells its two passes apart by E alone, so the
; wordmark bytes must live entirely above the header-logo range.
ASSERT LOW(HeroLogo) > $34
ASSERT LOW(HeroLogoEnd) > LOW(HeroLogo)
ASSERT HIGH(HeroLogo) == 0

SECTION "speed", ROM0[SPEED_ADDR]
    db 60                   ; scroll frames; short boot patches this to 20

SECTION "handoff", ROM0[$00FE]
Handoff:
    ldh [$FF50], a          ; unmap boot ROM; execution falls into $0100
