import json
import unittest
from unittest.mock import patch
import ets_auto_answer as a

class Client:
    def __init__(self, states): self.states=iter(states); self.last={}; self.clicks=0
    def evaluate(self, expression):
        if 'target.click()' in expression: return ''
        self.last=next(self.states,self.last)
        return json.dumps(self.last)
    def click(self,x,y): self.clicks+=1

def state(selected, nav=False, count=None):
    return {'id':'1_1','selected':selected,'anySelected':selected,'recorded':False,'navFound':nav,'answeredCount':count}

class ConfirmationTests(unittest.TestCase):
    def setUp(self): self.question={'id':'1_1','order':1,'options':[{'clicks':[]}]}
    def test_diagnostic_distinguishes_visual_and_ets_extraction_classes(self):
        state_data=state(True)
        state_data["cls"]="choose2 choose2_selected"
        result=a.answer_confirmation_diagnostic(Client([state_data]),self.question,"A")
        self.assertIn("visual_selection_class=True",result)
        self.assertIn("ets_extraction_class=False",result)
        state_data["cls"] += " choose_selected"
        result=a.answer_confirmation_diagnostic(Client([state_data]),self.question,"A")
        self.assertIn("ets_extraction_class=True",result)

    @patch.object(a.time,'sleep')
    def test_stable_selected_without_navigation_is_not_enough(self,_):
        c=Client([state(True),state(True),state(True)])
        self.assertFalse(a.choose_answer(c,self.question,'A'))
    @patch.object(a.time,'sleep')
    def test_stable_extraction_marker_on_listening_page_is_accepted(self,_):
        selected=state(True); selected["cls"]="choose2 choose2_selected choose_selected"
        c=Client([selected,selected,selected])
        self.assertTrue(a.choose_answer(c,self.question,'A'))
        self.assertEqual(c.clicks,0)

    @patch.object(a.time,'sleep')
    def test_visual_marker_without_extraction_marker_is_rejected(self,_):
        visual=state(True); visual["cls"]="choose2 choose2_selected"
        self.assertFalse(a.choose_answer(Client([visual,visual,visual]),self.question,'A'))

    @patch.object(a.time,'sleep')
    def test_unstable_extraction_marker_is_rejected(self,_):
        selected=state(True); selected["cls"]="choose2 choose_selected"
        lost=state(True); lost["cls"]="choose2 choose2_selected"
        self.assertFalse(a.choose_answer(Client([selected,selected,lost]),self.question,'A'))

    @patch.object(a.time,'sleep')
    def test_existing_navigation_requires_completion_evidence(self,_):
        c=Client([state(True,True),state(True,True)])
        self.assertFalse(a.choose_answer(c,self.question,'A'))
    @patch.object(a.time,'sleep')
    def test_disappearing_selection_is_not_confirmed(self,_):
        c=Client([state(True),state(True),state(False)])
        self.assertFalse(a.choose_answer(c,self.question,'A'))

if __name__=='__main__': unittest.main()
