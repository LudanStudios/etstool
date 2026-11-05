"""Timing regression tests; no real ETS, credentials, devices, or audio output."""
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import wave

import ets_speaking_auto as auto
import ets_auto_answer as answers


class SpeakingTimingTests(unittest.TestCase):
    def setUp(self):
        base = Path(__file__).resolve().parents[1] / ".build" / "test-temp"
        base.mkdir(parents=True, exist_ok=True)
        self.directory = tempfile.TemporaryDirectory(dir=base)
        self.root = Path(self.directory.name)

    def tearDown(self):
        self.directory.cleanup()

    def wav(self, seconds):
        path = self.root / f"{seconds}.wav"
        with wave.open(str(path), "wb") as output:
            output.setparams((1, 2, 1000, 0, "NONE", "not compressed"))
            output.writeframes(b"\0\0" * round(seconds * 1000))
        return path

    def test_reload_rejects_missing_settings_instead_of_resetting_defaults(self):
        path = self.root / "answer_config.yaml"
        self.assertEqual(answers.load_answer_config(path), (1.0, "default", 0.0, False))
        with self.assertRaises(FileNotFoundError):
            answers.load_answer_config(path, allow_missing=False)
        path.write_text("accuracy_rate: 0.75\nrandom_seed: fixed\nskip_interval_seconds: 2\n", encoding="utf-8")
        self.assertEqual(answers.load_answer_config(path, allow_missing=False), (0.75, "fixed", 2.0, False))

    def test_natural_speed_with_room_and_smallest_fitting_speed(self):
        self.assertEqual(auto.playback_speed_factor(self.wav(3.1), 15), 1)
        path = self.wav(34)
        speed = auto.playback_speed_factor(path, 30)
        self.assertEqual(speed, 1.19)
        self.assertLessEqual(34 / speed, auto.usable_recording_seconds(30))
        self.assertGreater(34 / (speed - .01), auto.usable_recording_seconds(30))
        self.assertEqual(auto.playback_speed_factor(self.wav(43.125), 30), 1.5)
        with self.assertRaises(auto.TimingBudgetError):
            auto.playback_speed_factor(self.wav(44), 30)

    def test_only_record_steps_with_exact_question_count_are_mapped(self):
        template = self.root / "123" / "template_1"
        template.mkdir(parents=True)
        (template / "ctrl.json").write_text(json.dumps([
            {"filetype": "wait", "order": "1", "playtime": "100"},
            {"filetype": "action.record", "order": "3", "playtime": "30"},
            {"filetype": "action.record", "order": "2", "playtime": "15"},
        ]), encoding="utf-8")
        self.assertEqual(auto.read_recording_limits(self.root, "123", 2), [15, 30])
        self.assertEqual(auto.read_recording_limits(self.root, "123", 3), [])
        self.assertEqual(auto.recording_budget({"recording_remaining_seconds": 20}, 30), 20)
        self.assertEqual(auto.recording_budget({"recording_remaining_seconds": None}, 30), 30)

    def test_oversize_requests_regeneration_before_connecting_to_ets(self):
        session = self.root / "session.json"
        session.write_text(json.dumps({"PackId": "123", "Items": [
            {"WavPath": str(self.wav(60)), "RecordingSeconds": 30}]}), encoding="utf-8")
        player = self.root / "fake-player.exe"
        player.touch()
        with patch.object(auto, "devtools_targets") as targets, patch.object(auto, "log"):
            code = auto._main(["--session", str(session), "--data-root", str(self.root), "--player", str(player)])
        self.assertEqual(code, 10)
        targets.assert_not_called()
        request = json.loads(Path(str(session) + ".timing-request.json").read_text())
        self.assertEqual(request, {"PackId": "123", "Index": 0, "RecordingSeconds": 30})

    def run_mock_recording(self, closing_time=31, transient_gap=False):
        clock = [0.0]
        session = self.root / "session.json"
        session.write_text(json.dumps({"PackId": "123", "Items": [
            {"WavPath": str(self.wav(34)), "RecordingSeconds": 30}]}), encoding="utf-8")
        player_path = self.root / "fake-player.exe"
        player_path.touch()
        launched = []

        class Client:
            def __init__(self, _): pass
            def evaluate(self, _): return "https://test/#/modelTestMode?set_id=123"
            def close(self): pass

        class Player:
            def __init__(self, args, **kwargs):
                launched.append(args)
                self.end = clock[0] + 34 / float(args[2])
                self.returncode = None
            def poll(self):
                if clock[0] >= self.end: self.returncode = 0
                return self.returncode
            def communicate(self, **kwargs): return "PLAY\nDONE\n", None
            def terminate(self): self.returncode = -1
            def wait(self, **kwargs): return self.returncode

        def state(_):
            active = 1 <= clock[0] < closing_time
            if transient_gap and 10 <= clock[0] < 10.3: active = False
            return {"text": "recording" if active else "preparing", "recording": active,
                    "recording_remaining_seconds": (30 if active and not launched else None),
                    "exam": False, "stop": False, "play": False, "fix": False, "next": False}

        def sleep(seconds): clock[0] += seconds
        with patch.object(auto, "CDPClient", Client), patch.object(auto, "page_state", state), \
             patch.object(auto, "devtools_targets", return_value=[{"webSocketDebuggerUrl": "fake"}]), \
             patch.object(auto.subprocess, "Popen", Player), patch.object(auto.time, "monotonic", side_effect=lambda: clock[0]), \
             patch.object(auto.time, "sleep", side_effect=sleep), patch.object(auto, "log"):
            result = auto._main(["--session", str(session), "--data-root", str(self.root), "--player", str(player_path), "--keep-temp"])
        self.assertEqual(len(launched), 1)
        self.assertEqual(launched[0][2], "1.19")
        return result

    def test_mock_waits_for_real_recording_end_and_tolerates_short_dom_gap(self):
        self.assertEqual(self.run_mock_recording(transient_gap=True), 0)

    def test_actual_early_close_still_fails_instead_of_marking_complete(self):
        self.assertEqual(self.run_mock_recording(closing_time=20), 9)


if __name__ == "__main__":
    unittest.main()
