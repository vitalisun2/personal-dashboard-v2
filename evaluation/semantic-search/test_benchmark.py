import math
import unittest

from benchmark import aggregate, metrics


class EvaluationMetricsTests(unittest.TestCase):
    def test_complete_relevant_set_in_ideal_order(self):
        query = {"relevant": [{"id": "a", "grade": 2}, {"id": "b", "grade": 1}], "hardNegatives": ["c"]}
        measured = metrics(query, ["a", "b"])
        self.assertEqual(measured["recallAt5"], 1)
        self.assertEqual(measured["precisionReturned"], 1)
        self.assertEqual(measured["ndcgAt5"], 1)
        self.assertIsNone(measured["noAnswerCorrect"])

    def test_wrong_first_result_penalizes_rank_and_precision(self):
        query = {"relevant": [{"id": "a", "grade": 2}], "hardNegatives": ["c"]}
        measured = metrics(query, ["c", "a"])
        self.assertEqual(measured["recallAt5"], 1)
        self.assertEqual(measured["precisionReturned"], .5)
        self.assertAlmostEqual(measured["ndcgAt5"], 1 / math.log2(3))
        self.assertEqual(measured["reciprocalRank"], .5)
        self.assertEqual(measured["hardNegativeCount"], 1)

    def test_no_answer_is_not_in_positive_query_recall(self):
        query = {"relevant": [], "hardNegatives": ["c"]}
        correct = metrics(query, [])
        incorrect = metrics(query, ["c"])
        self.assertIsNone(correct["recallAt5"])
        self.assertTrue(correct["noAnswerCorrect"])
        self.assertFalse(incorrect["noAnswerCorrect"])
        self.assertEqual(incorrect["precisionReturned"], 0)

    def test_missing_answer_is_zero_not_perfect_empty_precision(self):
        query = {"relevant": [{"id": "a", "grade": 1}], "hardNegatives": []}
        measured = metrics(query, [])
        self.assertEqual(measured["precisionReturned"], 0)
        self.assertEqual(measured["recallAt5"], 0)

    def test_sixth_result_does_not_count_as_found(self):
        query = {"relevant": [{"id": "a", "grade": 1}], "hardNegatives": []}
        self.assertEqual(metrics(query, ["b", "c", "d", "e", "f", "a"])["recallAt5"], 0)

    def test_split_metrics_do_not_mix_test_into_development(self):
        query = {"relevant": [{"id": "a", "grade": 1}], "hardNegatives": []}
        summary = aggregate([
            {"split": "dev", "metrics": metrics(query, ["a"]), "elapsedMs": 100},
            {"split": "test", "metrics": metrics(query, []), "elapsedMs": 200},
        ])
        self.assertEqual(summary["dev"]["recallAt5"], 1)
        self.assertEqual(summary["test"]["recallAt5"], 0)
        self.assertEqual(summary["all"]["recallAt5"], .5)


if __name__ == "__main__":
    unittest.main()
