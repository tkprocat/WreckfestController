# Tesseract OCR Training Data - Summary

## What We've Done

### 1. **Code Improvements** (Already Applied)
✅ Character whitelist for console output (ConsoleOcr.cs:42)
✅ 2x image upscaling for better recognition (ConsoleOcr.cs:103)
✅ Adjusted threshold from 128→100 (ConsoleOcr.cs:136)
✅ Space preservation for structured data (ConsoleOcr.cs:43)

### 2. **Ground Truth Training Data Created**
✅ 3 screenshot pairs (PNG + ground truth TXT files):
   - `ocr_debug_20251026_123943` - 10 players with various vehicles
   - `ocr_debug_20251026_124409` - 10 players with different vehicle assignments
   - `ocr_debug_20251026_182014` - Empty server (useful for edge cases)

## Current Training Data Quality

**Strengths:**
- Multiple player names (Bandit, Nahkatatti, STRmods, MaXeLL, nurjo, etc.)
- Various vehicle names (Sweeper, Bulldog, RoadSlayer, Dominator, Hammerhead, Grand Duke)
- Different numeric values (scores: 0, 106, 183, 204, 293, 326)
- Console messages (join/quit/timeout messages)
- Special characters (asterisks *, underscores _, brackets [], parentheses ())

**Gaps (Need More Data For):**
- Real player names (non-bot names without asterisks)
- High scores (4-5 digit numbers)
- Various ping values (currently all show 0)
- Different player statuses besides "ready"
- More vehicle name variety

## Next Steps

### Immediate: Test Current Improvements
The code improvements may already provide significant accuracy gains without custom training. Test by:
1. Running the server with players
2. Check OCR logs for accuracy
3. Compare OCR extracted text vs ground truth

### Short-term: Collect More Training Data
To improve training data diversity:

**Method 1: During Real Gameplay**
- Let real players join
- Trigger OCR during active races (when scores accumulate)
- Capture screenshots with varied player counts (1-24 players)

**Method 2: Manual Screenshot Collection**
1. Start server: `POST http://localhost:5100/api/server/start`
2. Add some bots: `dotnet run --project AddBotsApp`
3. Wait for server to stabilize
4. Start a race (this will generate scores and vehicle assignments)
5. Manually trigger OCR by using console commands
6. Collect 20-30 more diverse screenshots

**Method 3: Create synthetic variations**
- Use existing screenshots
- Manually edit ground truth with realistic variations
- This gives Tesseract more examples without running server

### Long-term: Full Tesseract Training

Once you have 30+ diverse screenshot pairs:

```bash
# 1. Install tesstrain
git clone https://github.com/tesseract-ocr/tesstrain
cd tesstrain

# 2. Copy your training data
mkdir -p data/wreckfest-ground-truth
cp bin/Debug/net8.0/ocr_debug_*.png data/wreckfest-ground-truth/
cp bin/Debug/net8.0/ocr_debug_*.gt.txt data/wreckfest-ground-truth/

# 3. Train the model (fine-tune English model)
make training MODEL_NAME=wreckfest START_MODEL=eng TESSDATA=../tessdata

# 4. Copy trained model
cp data/wreckfest.traineddata ../tessdata/

# 5. Update code to use custom model
# In ConsoleOcr.cs line 34, change "eng" to "wreckfest"
```

## Monitoring OCR Performance

Check these logs to monitor accuracy:
- `OCR extracted text preview: ...` - Shows what Tesseract read
- `Parsed player from OCR: ...` - Shows successfully parsed players
- `OCR extracted N players` - Should match actual player count

Compare with player list API: `GET http://localhost:5100/api/server/players`

## Expected Results

**With current improvements only:**
- ~75-85% character accuracy
- Successfully parses most player names
- May struggle with similar characters (l/I, 0/O, 1/l)

**With custom training (30+ screenshots):**
- ~90-95% character accuracy
- Excellent player name recognition
- Handles console-specific formatting well
- Minimal confusion on similar characters

**With extensive training (100+ screenshots):**
- ~95-98% character accuracy
- Near-perfect recognition of all console elements
- Handles edge cases (long names, special characters)
- Robust against varying image quality

## Tips for Better Training Data

1. **Diversity is key** - Different player counts, names, scores
2. **Quality matters** - Clear screenshots, good contrast
3. **Edge cases** - Include unusual names, max players, empty server
4. **Consistency** - Same console window size/font for all screenshots
5. **Accuracy** - Ground truth must be 100% accurate (every character, space, and symbol)

## Files Reference

All training-related files in `bin/Debug/net8.0/`:
- `ocr_debug_*.png` - Screenshots
- `ocr_debug_*.gt.txt` - Ground truth transcriptions
- `TESSERACT_TRAINING_README.md` - Detailed training instructions
- `TRAINING_DATA_SUMMARY.md` - This file
