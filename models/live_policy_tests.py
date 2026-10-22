"""Checks automatic latency decisions without GPU/model dependencies."""
import unittest
import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parent))
from live_policy import LiveSizePolicy

class LivePolicyTests(unittest.TestCase):
    def test_full_hd_request_starts_at_live_profile(self):
        policy=LiveSizePolicy()
        self.assertEqual(policy.choose(2,3,1920,True),640)
        policy.observe(2000)  # Cold graph compilation must not reduce quality.
        self.assertEqual(policy.choose(2,3,1920,True),640)
        policy.observe(480);policy.observe(470)
        self.assertEqual(policy.choose(2,3,1920,True),320)
        for _ in range(10):policy.observe(250)
        self.assertEqual(policy.choose(2,3,1920,True),320)

    def test_manual_choice_is_never_reduced(self):
        policy=LiveSizePolicy()
        self.assertEqual(policy.choose(2,3,1920,False),1920)
        for _ in range(10):policy.observe(1000)
        self.assertEqual(policy.choose(2,3,1920,False),1920)

    def test_new_model_and_user_size_reset_policy(self):
        policy=LiveSizePolicy()
        self.assertEqual(policy.choose(3,3,1920,True),192)
        self.assertEqual(policy.choose(4,3,1920,True),640)
        self.assertEqual(policy.choose(4,3,192,True),192)
        self.assertEqual(policy.choose(4,1,128,True),128)
        for _ in range(20):policy.observe(1000)
        self.assertEqual(policy.choose(4,1,128,True),128)

if __name__=='__main__':unittest.main()
