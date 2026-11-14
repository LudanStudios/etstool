import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
import ets_answer_listener as listener

class SelectionSwitchTests(unittest.TestCase):
    def test_listener_stops_and_restarts_answer_worker(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            marker = root / '_assistant' / 'automatic_selection.disabled'
            marker.parent.mkdir()
            events = []
            class Process:
                def poll(self): return None
            def start():
                events.append('start')
                return Process()
            def stop(process, label, **kwargs):
                if label == 'auto-answer listener': events.append('stop')
            def tick(seconds):
                if len(events) == 1: marker.write_text('disabled')
                elif len(events) == 2: marker.unlink()
                else: raise KeyboardInterrupt()
            with patch.object(listener, 'acquire_instance_mutex', return_value=True), \
                 patch.object(listener, 'ets_is_running', return_value=True), \
                 patch.object(listener, 'start_converter_watcher', return_value=Process()), \
                 patch.object(listener, 'start_auto_answer_worker', side_effect=start), \
                 patch.object(listener, 'stop_process', side_effect=stop), \
                 patch.object(listener, 'start_assistant_window'), \
                 patch.object(listener.time, 'sleep', side_effect=tick), \
                 patch.object(listener, 'log'):
                with self.assertRaises(KeyboardInterrupt):
                    listener.main(['--root', str(root), '--ets-exe', str(root / 'Ets.exe')])
            self.assertEqual(events, ['start', 'stop', 'start'])

if __name__ == '__main__': unittest.main()
